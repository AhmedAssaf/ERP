#!/usr/bin/env bash
# W-19, N-07: restore from the encrypted backups in OCI Object Storage (Jeddah). Three modes:
#
#   sudo infra/pilot/restore.sh --list
#       the PostgreSQL backup stamps available, newest last
#   sudo infra/pilot/restore.sh --verify [STAMP]
#       the monthly restore test (N-07), safe on the live VM: downloads a backup (latest by default), restores both
#       databases into a throwaway PostgreSQL container with no network and no volume, runs integrity checks, compares
#       the MinIO mirror with the live bucket, and removes everything it created
#   sudo infra/pilot/restore.sh --full [STAMP] [--force]
#       disaster recovery on a NEW VM (or after `docker compose down -v`): restores PostgreSQL and the MinIO bucket into
#       the pilot's own volumes; then run deploy.sh with the same image tag. Refuses to overwrite a platform database
#       that already holds data unless --force.
#
# Prerequisites for --full (docs/19 section 7): bootstrap.sh run, the checkout at the backup's image tag (MANIFEST), the
# images loaded, infra/pilot/.env and secrets/key-ring.pfx restored from the password manager. The target is the pilot
# tenant back within one hour (W-19 acceptance).
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

stamps() {
  dc --profile tools run --rm -T --no-deps rclone lsf --dirs-only vault:postgres | tr -d '/\r' | grep -E '^20.*Z$' | sort
}

download() {
  [ -n "$STAMP" ] || STAMP="$(stamps | tail -n 1)"
  [ -n "$STAMP" ] || die "no backup found in vault:postgres"
  RESTORE_DIR="$BACKUP_DIR/restore-$STAMP"
  mkdir -p "$RESTORE_DIR"
  log "downloading backup $STAMP"
  dc --profile tools run --rm -T --no-deps rclone copy "vault:postgres/$STAMP" "/backup/restore-$STAMP" --stats-one-line --stats 0
  (cd "$RESTORE_DIR" && sha256sum -c --quiet SHA256SUMS) || die "checksum mismatch in $RESTORE_DIR"
  log "checksums match; $(grep image_tag "$RESTORE_DIR/MANIFEST")"
}

# restore_into <container id>: globals, then both databases from the downloaded dumps.
restore_into() {
  local cid="$1"
  log "restoring roles (errors for roles that already exist are expected)"
  docker exec -i "$cid" sh -c 'psql -X -q -U "$POSTGRES_USER" -d postgres' < "$RESTORE_DIR/globals.sql" > /dev/null 2>&1 || true
  log "recreating the databases empty and restoring the dumps"
  docker exec -i "$cid" sh -c 'psql -X -q -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres' <<'SQL'
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
  docker exec -i "$cid" sh -c 'psql -X -q -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres' <<'SQL'
alter database keycloak owner to keycloak;
SQL
}

integrity_checks() {
  local cid="$1"
  docker exec -i "$cid" sh -c 'psql -X -A -t -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d platform' <<'SQL'
select 'tenants=' || count(*) from tenancy.tenants;
select 'tenant_hosts=' || count(*) from tenancy.tenant_hosts;
select 'members=' || count(*) from identity.members;
select 'vendor_companies=' || count(*) from vendor.companies;
select 'data_protection_keys=' || count(*) from platform.data_protection_keys;
select 'roles_present=' || string_agg(rolname, ',' order by rolname) from pg_roles where rolname in ('erp_app', 'erp_key_ring', 'erp_worker', 'keycloak');
select 'rls_forced_tables=' || count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace where c.relkind = 'r' and c.relforcerowsecurity;
SQL
  docker exec -i "$cid" sh -c 'psql -X -A -t -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d keycloak' <<'SQL'
select 'keycloak_realms=' || string_agg(name, ',' order by name) from realm;
select 'keycloak_users=' || count(*) from user_entity;
SQL
}

case "$MODE" in
  --list)
    stamps
    ;;

  --verify)
    started=$(date +%s)
    download
    image="$(dc config --images postgres | head -n 1)"
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
    log "integrity checks (restored copy):"
    integrity_checks "$name" | sed 's/^/  /'
    log "MinIO: live bucket against the mirror"
    live="$(dc --profile tools run --rm -T --no-deps rclone size "minio:$BUCKET" --exclude 'staging/**' --json)"
    mirror="$(dc --profile tools run --rm -T --no-deps rclone size vault:minio/current --json)"
    log "  live:   $live"
    log "  mirror: $mirror"
    [ "$(jq .count <<<"$live")" -le "$(jq .count <<<"$mirror")" ] || warn "the mirror holds fewer objects than the live bucket (new uploads since the last backup?)"
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
    log "database roles: passwords from .env (bootstrap.sql)"
    ERP_APP_DB_VERIFIER="$(env_value ERP_APP_DB_PASSWORD | scram_verifier)"
    KEYCLOAK_DB_VERIFIER="$(env_value KEYCLOAK_DB_PASSWORD | scram_verifier)"
    export ERP_APP_DB_VERIFIER KEYCLOAK_DB_VERIFIER
    PSQL_ENV="ERP_APP_DB_VERIFIER KEYCLOAK_DB_VERIFIER" psql_owner < "$PILOT_DIR/postgres/bootstrap.sql"
    unset ERP_APP_DB_VERIFIER KEYCLOAK_DB_VERIFIER
    integrity_checks "$cid" | sed 's/^/  /'
    log "MinIO: bucket and users, then the objects from vault:minio/current"
    dc --profile tools run --rm -T --no-deps minio-init
    dc --profile tools run --rm -T --no-deps -e RCLONE_MINIO_WRITE=1 rclone copy vault:minio/current "minio:$BUCKET" \
      --fast-list --stats-one-line --stats 0
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
