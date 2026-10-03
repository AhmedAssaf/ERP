#!/usr/bin/env bash
# W-19, N-07: nightly backup of the pilot to OCI Object Storage in Jeddah, encrypted on the VM before upload.
# Run by waslabid-backup.timer (systemd/, installed by bootstrap.sh); by hand: sudo infra/pilot/backup.sh
#
#   postgres/<stamp>/   globals.sql (roles, no passwords), platform.dump and keycloak.dump (pg_dump custom format),
#                       SHA256SUMS; a full copy every night, kept BACKUP_RETENTION_DAYS (35, N-07)
#   minio/current/      the bucket mirrored (rclone sync), without staging/ (expires after 2 days anyway, V-9)
#   minio/versions/<stamp>/  objects that the sync replaced or deleted, kept BACKUP_RETENTION_DAYS
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

log "dumping PostgreSQL into $WORK"
cid="$(container_of postgres)"
[ -n "$cid" ] || die "postgres is not running"
docker exec "$cid" sh -c 'exec pg_dumpall -U "$POSTGRES_USER" --globals-only --no-role-passwords' > "$WORK/globals.sql"
for db in platform keycloak; do
  docker exec "$cid" sh -c 'exec pg_dump -U "$POSTGRES_USER" -d "$0" --format=custom --compress=6' "$db" > "$WORK/$db.dump"
  # A dump that pg_restore cannot list is not a backup.
  docker exec -i "$cid" pg_restore --list < "$WORK/$db.dump" > /dev/null || die "$db.dump is unreadable"
done
cat > "$WORK/MANIFEST" <<EOF
stamp=$STAMP
image_tag=$TAG
postgres=$(docker exec "$cid" postgres --version)
EOF
(cd "$WORK" && sha256sum globals.sql platform.dump keycloak.dump MANIFEST > SHA256SUMS)
log "dump sizes: $(du -sh "$WORK" | cut -f1)"

# Paths inside the rclone container: $BACKUP_DIR is mounted at /backup.
log "uploading the dumps (encrypted) to vault:postgres/$STAMP"
dc --profile tools run --rm -T --no-deps rclone copy "/backup/$STAMP" "vault:postgres/$STAMP" --stats-one-line --stats 0

log "mirroring MinIO bucket $BUCKET to vault:minio/current (replaced and deleted objects to minio/versions/$STAMP)"
dc --profile tools run --rm -T --no-deps rclone sync "minio:$BUCKET" "vault:minio/current" \
  --backup-dir "vault:minio/versions/$STAMP" --exclude "staging/**" --fast-list --stats-one-line --stats 0

# By folder stamp, not by object age: an object moved to minio/versions keeps its original modification time.
CUTOFF="$(date -u -d "-${RETENTION} days" +%Y-%m-%dT%H%M%SZ)"
log "removing remote folders older than $RETENTION days (before $CUTOFF)"
for area in postgres minio/versions; do
  dc --profile tools run --rm -T --no-deps rclone lsf --dirs-only "vault:$area" 2>/dev/null | tr -d '/\r' | while read -r folder; do
    case "$folder" in 20*Z) ;; *) continue ;; esac
    if [[ "$folder" < "$CUTOFF" ]]; then
      log "  purge vault:$area/$folder"
      dc --profile tools run --rm -T --no-deps rclone purge "vault:$area/$folder" < /dev/null
    fi
  done
done

log "keeping the two newest local dump folders"
find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d -name '20*Z' | sort | head -n -2 | xargs -r rm -rf

date -u +%s > "$STATE_DIR/last-backup"
echo "$STAMP" > "$STATE_DIR/last-backup-stamp"
log "backup $STAMP done"
