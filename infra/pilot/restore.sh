#!/usr/bin/env bash
# W-19, N-07: restore from the encrypted backups in OCI Object Storage (Jeddah). Three modes:
#
#   sudo infra/pilot/restore.sh --list
#       every snapshot stamp, oldest first, marked complete or INCOMPLETE (a backup run that died mid-upload); only a
#       complete snapshot is ever restored, and without a STAMP the newest complete one
#   sudo infra/pilot/restore.sh --verify [STAMP]
#       the monthly restore test (N-07), safe on the live VM: downloads a snapshot (latest by default), restores both
#       databases into a throwaway PostgreSQL container with no network and no volume, applies bootstrap.sql exactly as
#       --full does, runs the integrity checks (including that every role may still connect), checks that every object
#       the snapshot lists is in the object store, and removes everything it created
#   sudo infra/pilot/restore.sh --full [STAMP] [--force]
#       disaster recovery on a NEW VM (or after `docker compose down -v`): restores PostgreSQL and the MinIO bucket into
#       the pilot's own volumes; then run deploy.sh with the same image tag. Refuses to overwrite a platform database
#       that already holds data unless --force.
#
# Prerequisites for --full (docs/19 section 7): bootstrap.sh run, the checkout at the snapshot's image tag (MANIFEST),
# the images loaded, infra/pilot/.env and secrets/key-ring.pfx restored from the password manager. The target is the
# pilot tenant back within one hour (W-19 acceptance).
set -euo pipefail
# shellcheck source=infra/pilot/lib.sh
. "$(dirname "$0")/lib.sh"

MODE="${1:-}"; shift || true
STAMP=""
FORCE=0
for arg in "$@"; do
  case "$arg" in
    --force) FORCE=1 ;;
    20*Z) STAMP="$arg" ;;
    *) die "unknown argument $arg" ;;
  esac
done

require_root
require_env_file
BACKUP_DIR="$(env_value BACKUP_DIR)"; BACKUP_DIR="${BACKUP_DIR:-/var/backups/waslabid}"
BUCKET="$(env_value STORAGE_BUCKET)"
TAG="$(cat "$STATE_DIR/deployed-tag" 2>/dev/null || git -C "$REPO_ROOT" rev-parse --short=12 HEAD)"
export IMAGE_TAG="$TAG"
umask 077

# rclone runs in its container; $BACKUP_DIR is mounted there at /backup.
rc() { dc --profile tools run --rm -T --no-deps "${RC_ENV[@]}" rclone "$@" < /dev/null; }
RC_ENV=()

# complete_stamps: snapshots whose COMPLETE marker was uploaded (backup.sh writes it last), oldest first.
complete_stamps() {
  rc lsf -R --files-only --include "*/COMPLETE" vault:nightly | tr -d '\r' | sed -n 's#^\(20[^/]*Z\)/COMPLETE$#\1#p' | sort
}

all_stamps() {
  rc lsf --dirs-only vault:nightly | tr -d '/\r' | grep -E '^20.*Z$' | sort
}

download() {
  local complete
  complete="$(complete_stamps)"
  [ -n "$STAMP" ] || STAMP="$(tail -n 1 <<<"$complete")"
  [ -n "$STAMP" ] || die "no complete snapshot in vault:nightly (restore.sh --list)"
  grep -qx "$STAMP" <<<"$complete" \
    || die "snapshot $STAMP is incomplete (no COMPLETE marker: its backup run died mid-upload); pick another (--list)"
  RESTORE_DIR="$BACKUP_DIR/restore-$STAMP"
  mkdir -p "$RESTORE_DIR"
  log "downloading snapshot $STAMP"
  rc copy "vault:nightly/$STAMP" "/backup/restore-$STAMP" --stats-one-line --stats 0
  (cd "$RESTORE_DIR" && sha256sum -c --quiet SHA256SUMS) || die "checksum mismatch in $RESTORE_DIR"
  log "checksums match; $(grep image_tag "$RESTORE_DIR/MANIFEST"), $(grep minio_objects "$RESTORE_DIR/MANIFEST")"
}

# psql_in <container> <database> [psql args]: SQL on stdin, as the owner over the container's local socket.
psql_in() {
  local cid="$1" db="$2"; shift 2
  docker exec -i "$cid" sh -c 'exec psql -X -q -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$0" "$@"' "$db" "$@"
}

# restore_into <container>: globals, then both databases from the downloaded dumps. A plain pg_restore (no --create)
# brings no database-level grants back; bootstrap_roles restores them.
restore_into() {
  local cid="$1"
  log "restoring roles (errors for roles that already exist are expected)"
  docker exec -i "$cid" sh -c 'psql -X -q -U "$POSTGRES_USER" -d postgres' < "$RESTORE_DIR/globals.sql" > /dev/null 2>&1 || true
  log "recreating the databases empty and restoring the dumps"
  psql_in "$cid" postgres <<'SQL'
select count(pg_terminate_backend(pid)) as terminated from pg_stat_activity where datname in ('platform', 'keycloak') and pid <> pg_backend_pid() \gset
drop database if exists platform;
drop database if exists keycloak;
create database platform template template0 encoding 'UTF8';
create database keycloak template template0 encoding 'UTF8';
SQL
  for db in platform keycloak; do
    docker exec -i "$cid" sh -c 'exec pg_restore -U "$POSTGRES_USER" -d "$0" --exit-on-error --single-transaction' "$db" \
      < "$RESTORE_DIR/$db.dump"
    log "  $db restored"
  done
}

# bootstrap_roles <container>: postgres/bootstrap.sql with the passwords from .env, as deploy.sh runs it: role
# passwords (globals.sql carries none), database owners and every database-level CONNECT grant.
bootstrap_roles() {
  local cid="$1"
  ERP_APP_DB_VERIFIER="$(env_value ERP_APP_DB_PASSWORD | scram_verifier)"
  KEYCLOAK_DB_VERIFIER="$(env_value KEYCLOAK_DB_PASSWORD | scram_verifier)"
  export ERP_APP_DB_VERIFIER KEYCLOAK_DB_VERIFIER
  docker exec -i -e ERP_APP_DB_VERIFIER -e KEYCLOAK_DB_VERIFIER "$cid" \
    sh -c 'exec psql -X -q -t -A -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d platform' < "$PILOT_DIR/postgres/bootstrap.sql"
  unset ERP_APP_DB_VERIFIER KEYCLOAK_DB_VERIFIER
}

# integrity_checks <container>: row counts for the log, and hard failures for what would keep a host from starting.
integrity_checks() {
  local cid="$1"
  # `|| return 1` on each: a caller may run this in a condition, where set -e does not stop at the first failure.
  psql_in "$cid" platform -A -t <<'SQL' || return 1
select 'tenants=' || count(*) from tenancy.tenants;
select 'tenant_hosts=' || count(*) from tenancy.tenant_hosts;
select 'members=' || count(*) from identity.members;
select 'vendor_companies=' || count(*) from vendor.companies;
select 'data_protection_keys=' || count(*) from platform.data_protection_keys;
select 'rls_forced_tables=' || count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace where c.relkind = 'r' and c.relforcerowsecurity;
select 'connect=' || string_agg(r || ':' || has_database_privilege(r, d, 'connect'), ',' order by r)
  from (values ('erp_app', 'platform'), ('erp_key_ring', 'platform'), ('erp_worker', 'platform'), ('keycloak', 'keycloak')) v(r, d)
 where exists (select from pg_roles where rolname = r);
do $$
declare missing text;
begin
  select string_agg(r, ', ') into missing
    from (values ('erp_app'), ('erp_key_ring'), ('erp_worker'), ('keycloak')) v(r)
   where not exists (select from pg_roles where rolname = r);
  if missing is not null then raise exception 'roles missing after the restore: %', missing; end if;
  select string_agg(r, ', ') into missing
    from (values ('erp_app', 'platform'), ('erp_key_ring', 'platform'), ('erp_worker', 'platform'), ('keycloak', 'keycloak')) v(r, d)
   where not has_database_privilege(r, d, 'connect');
  if missing is not null then raise exception 'these roles cannot connect after the restore: %', missing; end if;
end $$;
SQL
  psql_in "$cid" keycloak -A -t <<'SQL' || return 1
select 'keycloak_realms=' || string_agg(name, ',' order by name) from realm;
select 'keycloak_users=' || count(*) from user_entity;
SQL
}

case "$MODE" in
  --list)
    complete="$(complete_stamps)"
    all_stamps | while read -r stamp; do
      if grep -qx "$stamp" <<<"$complete"; then echo "$stamp complete"; else echo "$stamp INCOMPLETE (never restored)"; fi
    done
    ;;

  --verify)
    started=$(date +%s)
    download
    image="$(service_image postgres)"
    name="waslabid-restore-test-$$"
    owner="$(env_value POSTGRES_USER)"
    log "starting a throwaway PostgreSQL ($image) with no network"
    docker run -d --rm --name "$name" --network none --memory 768m \
      -e POSTGRES_USER="$owner" -e POSTGRES_PASSWORD="$(head -c 24 /dev/urandom | base64 | tr -d '/+=')" -e POSTGRES_DB=postgres \
      "$image" > /dev/null
    trap 'docker rm -f "$name" >/dev/null 2>&1 || true; rm -rf "$RESTORE_DIR"' EXIT
    for _ in $(seq 1 60); do docker exec "$name" pg_isready -U "$owner" -q && break; sleep 2; done
    sleep 3
    restore_into "$name"
    bootstrap_roles "$name" > /dev/null
    log "integrity checks (restored copy):"
    integrity_checks "$name" | sed 's/^/  /'
    log "object store: every key the snapshot lists"
    rc lsf -R --files-only vault:minio/objects | tr -d '\r' | sort > "$RESTORE_DIR/stored.txt"
    missing="$(comm -23 "$RESTORE_DIR/minio-manifest.txt" "$RESTORE_DIR/stored.txt" | wc -l)"
    [ "$missing" -eq 0 ] || die "$missing object(s) listed by snapshot $STAMP are not in vault:minio/objects"
    log "  $(wc -l < "$RESTORE_DIR/minio-manifest.txt") listed, all stored"
    log "restore test of $STAMP passed in $(( $(date +%s) - started )) s; record it in docs/19 section 7"
    date -u +%s > "$STATE_DIR/last-restore-test"
    ;;

  --full)
    started=$(date +%s)
    download
    log "starting PostgreSQL and MinIO"
    dc up -d --wait --wait-timeout 300 postgres minio
    cid="$(container_of postgres)"
    existing="$(printf '%s\n' "select count(*) from information_schema.tables where table_schema = 'tenancy';" | psql_owner -t -A 2>/dev/null || echo 0)"
    if [ "${existing:-0}" != "0" ] && [ "$FORCE" -ne 1 ]; then
      die "the platform database already holds the tenancy schema; this is not an empty VM (use --force to overwrite it)"
    fi
    restore_into "$cid"
    log "database roles and grants: passwords from .env (bootstrap.sql)"
    bootstrap_roles "$cid"
    integrity_checks "$cid" | sed 's/^/  /'
    log "MinIO: bucket and users, then the objects snapshot $STAMP lists"
    dc --profile tools run --rm -T --no-deps minio-init
    RC_ENV=(-e RCLONE_MINIO_WRITE=1)
    rc copy vault:minio/objects "minio:$BUCKET" --files-from-raw "/backup/restore-$STAMP/minio-manifest.txt" \
      --no-traverse --stats-one-line --stats 0
    RC_ENV=()
    backup_tag="$(sed -n 's/^image_tag=//p' "$RESTORE_DIR/MANIFEST")"
    rm -rf "$RESTORE_DIR"
    log "data restored in $(( $(date +%s) - started )) s. Next: check out $backup_tag (or newer), load its images, then"
    log "  sudo infra/pilot/deploy.sh --tag $backup_tag"
    ;;

  *)
    sed -n '2,19p' "$0"
    exit 2
    ;;
esac
