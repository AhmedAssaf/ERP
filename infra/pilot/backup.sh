#!/usr/bin/env bash
# W-19, N-07: nightly backup of the pilot to OCI Object Storage in Jeddah, encrypted on the VM before upload.
# Run by waslabid-backup.timer (systemd/, installed by bootstrap.sh); by hand: sudo infra/pilot/backup.sh
#
#   nightly/<stamp>/   globals.sql (roles, no passwords), platform.dump and keycloak.dump (pg_dump custom format),
#                      minio-manifest.txt (every object key of the bucket at that time, staging/ excluded), MANIFEST,
#                      SHA256SUMS; a new folder every night, kept BACKUP_RETENTION_DAYS (35, N-07)
#   minio/objects/     every object once, never overwritten (the platform writes objects once: logos by hash, documents
#                      by id); a snapshot is restored by copying the keys its minio-manifest.txt lists
#
# Write-once by design, so the bucket can carry an OCI retention rule of BACKUP_RETENTION_DAYS (docs/19 step 4): nothing
# younger than that can be deleted or overwritten, not even with this VM's key, so a compromised VM cannot purge the
# off-VM copies. Pruning therefore waits one day longer than the rule.
#
# Not backed up: Elasticsearch (telemetry, rebuilt empty), Redis (counters only), ClamAV signatures, Caddy certificates
# (re-issued), and the secrets: infra/pilot/.env and secrets/key-ring.pfx live in the password manager, never in a backup
# (the key-ring certificate must stay apart from the database that holds the encrypted keys, W-24).
set -euo pipefail
# shellcheck source=infra/pilot/lib.sh
. "$(dirname "$0")/lib.sh"

require_root
# Installed by bootstrap.sh before the first deploy: nothing to do until then.
[ -f "$STATE_DIR/deployed-tag" ] || { echo "not deployed yet; nothing to do"; exit 0; }
require_env_file

BACKUP_DIR="$(env_value BACKUP_DIR)"; BACKUP_DIR="${BACKUP_DIR:-/var/backups/waslabid}"
RETENTION="$(env_value BACKUP_RETENTION_DAYS)"; RETENTION="${RETENTION:-35}"
PRUNE_AFTER=$((RETENTION + 1))
BUCKET="$(env_value STORAGE_BUCKET)"
TAG="$(cat "$STATE_DIR/deployed-tag" 2>/dev/null || true)"
[ -n "$TAG" ] || die "no deployed tag in $STATE_DIR/deployed-tag; deploy first"
export IMAGE_TAG="$TAG"

STAMP="$(date -u +%Y-%m-%dT%H%M%SZ)"
WORK="$BACKUP_DIR/$STAMP"
umask 077
mkdir -p "$WORK"
exec 9>"$STATE_DIR/backup.lock"
flock -n 9 || die "another backup is running"

# rclone runs in its container; $BACKUP_DIR is mounted there at /backup.
rc() { dc --profile tools run --rm -T --no-deps rclone "$@" < /dev/null; }

log "dumping PostgreSQL into $WORK"
cid="$(container_of postgres)"
[ -n "$cid" ] || die "postgres is not running"
docker exec "$cid" sh -c 'exec pg_dumpall -U "$POSTGRES_USER" --globals-only --no-role-passwords' > "$WORK/globals.sql"
for db in platform keycloak; do
  docker exec "$cid" sh -c 'exec pg_dump -U "$POSTGRES_USER" -d "$0" --format=custom --compress=6' "$db" > "$WORK/$db.dump"
  # A dump that pg_restore cannot list is not a backup.
  docker exec -i "$cid" pg_restore --list < "$WORK/$db.dump" > /dev/null || die "$db.dump is unreadable"
done

# After the dumps, so every object a dumped row points to is listed (uploads write the object before the row).
log "listing the MinIO bucket $BUCKET"
rc lsf -R --files-only --exclude "staging/**" "minio:$BUCKET" | tr -d '\r' | sort > "$WORK/minio-manifest.txt"

log "copying new objects (encrypted) to vault:minio/objects; existing ones are never overwritten"
rc copy "minio:$BUCKET" "vault:minio/objects" --files-from-raw "/backup/$STAMP/minio-manifest.txt" \
  --immutable --no-traverse --stats-one-line --stats 0
# An object deleted between the listing and the copy is not in the store: drop it from the snapshot, loudly.
rc lsf -R --files-only "vault:minio/objects" | tr -d '\r' | sort > "$BACKUP_DIR/stored-objects.txt"
missing="$(comm -23 "$WORK/minio-manifest.txt" "$BACKUP_DIR/stored-objects.txt" | wc -l)"
if [ "$missing" -gt 0 ]; then
  warn "$missing object(s) listed but not stored (deleted meanwhile?); left out of this snapshot"
  comm -12 "$WORK/minio-manifest.txt" "$BACKUP_DIR/stored-objects.txt" > "$WORK/minio-manifest.kept"
  mv "$WORK/minio-manifest.kept" "$WORK/minio-manifest.txt"
fi

cat > "$WORK/MANIFEST" <<EOF
stamp=$STAMP
image_tag=$TAG
postgres=$(docker exec "$cid" postgres --version)
minio_objects=$(wc -l < "$WORK/minio-manifest.txt")
EOF
(cd "$WORK" && sha256sum globals.sql platform.dump keycloak.dump minio-manifest.txt MANIFEST > SHA256SUMS)
log "snapshot size: $(du -sh "$WORK" | cut -f1), $(wc -l < "$WORK/minio-manifest.txt") objects"

log "uploading the snapshot (encrypted) to vault:nightly/$STAMP"
rc copy "/backup/$STAMP" "vault:nightly/$STAMP" --immutable --stats-one-line --stats 0

# Pruning: snapshot folders by their stamp, then objects no kept snapshot lists. A deletion the retention rule still
# refuses (an object uploaded less than RETENTION days ago) is only a warning; the next night tries again.
CUTOFF="$(date -u -d "-${PRUNE_AFTER} days" +%Y-%m-%dT%H%M%SZ)"
log "removing snapshots older than $PRUNE_AFTER days (before $CUTOFF)"
rc lsf --dirs-only "vault:nightly" | tr -d '/\r' | while read -r folder; do
  case "$folder" in 20*Z) ;; *) continue ;; esac
  if [[ "$folder" < "$CUTOFF" ]]; then
    log "  purge vault:nightly/$folder"
    rc purge "vault:nightly/$folder" || warn "could not purge vault:nightly/$folder yet"
  fi
done

log "removing stored objects that no kept snapshot lists and that are older than $PRUNE_AFTER days"
rm -rf "$BACKUP_DIR/manifests"
rc copy "vault:nightly" "/backup/manifests" --include "*/minio-manifest.txt" --stats 0
find "$BACKUP_DIR/manifests" -name minio-manifest.txt -exec cat {} + 2>/dev/null | sort -u > "$BACKUP_DIR/referenced.txt"
if [ -s "$BACKUP_DIR/referenced.txt" ] || [ ! -s "$WORK/minio-manifest.txt" ]; then
  rc lsf -R --files-only --min-age "${PRUNE_AFTER}d" "vault:minio/objects" | tr -d '\r' | sort > "$BACKUP_DIR/old-objects.txt"
  comm -23 "$BACKUP_DIR/old-objects.txt" "$BACKUP_DIR/referenced.txt" > "$BACKUP_DIR/unreferenced.txt"
  if [ -s "$BACKUP_DIR/unreferenced.txt" ]; then
    log "  $(wc -l < "$BACKUP_DIR/unreferenced.txt") object(s) to remove"
    rc delete "vault:minio/objects" --files-from-raw /backup/unreferenced.txt \
      || warn "some objects could not be removed yet (retention rule); the next backup tries again"
  fi
else
  warn "no kept snapshot manifest could be read; no object is removed"
fi
rm -rf "$BACKUP_DIR/manifests" "$BACKUP_DIR"/{stored-objects,referenced,old-objects,unreferenced}.txt

log "keeping the two newest local snapshot folders"
find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name '20*Z' | sort | head -n -2 | xargs -r rm -rf

date -u +%s > "$STATE_DIR/last-backup"
echo "$STAMP" > "$STATE_DIR/last-backup-stamp"
log "backup $STAMP done"
