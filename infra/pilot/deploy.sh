#!/usr/bin/env bash
# W-19: deploys (or re-deploys) the pilot on the VM. Idempotent: every step can run again, and running the whole script
# twice with the same tag changes nothing the second time.
#
#   sudo infra/pilot/deploy.sh --tag <git short sha>     # images loaded with build-images.sh
#   sudo infra/pilot/deploy.sh --tag <tag> --build       # build here (amd64 only until the Tailwind arm64 follow-up)
#
# Order (docs/19 section 5): databases and storage -> database roles -> one-shots (MinIO, Elasticsearch users) ->
# Keycloak and the collector -> the MIGRATOR, which must succeed before any host starts (it applies the migrations,
# installs Hangfire's tables, secures them with the jobs/ migrations and gives erp_key_ring and erp_worker their logins) ->
# web and worker -> Caddy -> verification. A failed step stops the script; hosts already running keep the old version.
set -euo pipefail
# shellcheck source=infra/pilot/lib.sh
. "$(dirname "$0")/lib.sh"

usage() { sed -n '2,12p' "$0"; exit 2; }

TAG=""
BUILD=0
while [ $# -gt 0 ]; do
  case "$1" in
    --tag) TAG="${2:?}"; shift 2 ;;
    --build) BUILD=1; shift ;;
    -h|--help) usage ;;
    *) usage ;;
  esac
done

require_root
require_env_file

# The SMTP login is read by Compose, the Keycloak realm import (JSON) and curl: refuse characters that break them.
for name in SMTP_USERNAME SMTP_PASSWORD; do
  case "$(env_value "$name")" in
    *'$'* | *'"'* | *'\'*) die "$name in .env contains \$, a double quote or a backslash; generate another SMTP credential (docs/19 step 4)" ;;
  esac
done
command -v docker >/dev/null || die "docker is not installed (bootstrap.sh)"
docker compose version >/dev/null || die "the docker compose plugin is missing (bootstrap.sh)"

[ -n "$TAG" ] || TAG="$(git -C "$REPO_ROOT" rev-parse --short=12 HEAD)"
export IMAGE_TAG="$TAG"
HEAD_TAG="$(git -C "$REPO_ROOT" rev-parse --short=12 HEAD 2>/dev/null || echo unknown)"
[ "$HEAD_TAG" = "$TAG" ] || warn "checkout is at $HEAD_TAG but the images are $TAG: the Compose file, Caddyfile and realm files come from the checkout"

# The Data Protection certificate (W-24), readable by the hosts' user (uid 1654) and nobody else.
PFX="$PILOT_DIR/secrets/key-ring.pfx"
[ -f "$PFX" ] || die "$PFX is missing (docs/19 step 5)"
[ "$(stat -c '%u %a' "$PFX")" = "1654 400" ] || die "$PFX must be owned by uid 1654 with mode 400: chown 1654:1654 $PFX && chmod 400 $PFX"

log "validating the Compose file"
dc --profile tools config --quiet

# Before anything changes: a Caddyfile or host list Caddy refuses would take every site down once Caddy is recreated,
# so the deploy stops here and the running Caddy keeps serving.
log "validating the Caddyfile with TENANT_HOSTS = $(normalise_hosts "$(env_value TENANT_HOSTS)")"
caddy_validate || die "Caddy refuses the Caddyfile with these values; nothing was changed"

PREFIX="$(env_value IMAGE_PREFIX)"; PREFIX="${PREFIX:-waslabid}"
if [ "$BUILD" -eq 1 ]; then
  log "building the application images $TAG on this machine"
  dc --profile tools build web worker migrator
fi
for image in web worker migrator; do
  docker image inspect "$PREFIX/$image:$TAG" >/dev/null 2>&1 \
    || die "image $PREFIX/$image:$TAG is not on this VM: run infra/pilot/build-images.sh $TAG <ssh target> on a build machine first"
done

log "pulling the pinned third-party images"
dc --profile tools pull --ignore-buildable --quiet

log "starting PostgreSQL, Redis, MinIO, Elasticsearch and ClamAV (ClamAV's first start downloads signatures)"
dc up -d --wait --wait-timeout 900 postgres redis minio elasticsearch clamav

log "database roles and extensions (postgres/bootstrap.sql)"
ERP_APP_DB_VERIFIER="$(env_value ERP_APP_DB_PASSWORD | scram_verifier)"
KEYCLOAK_DB_VERIFIER="$(env_value KEYCLOAK_DB_PASSWORD | scram_verifier)"
export ERP_APP_DB_VERIFIER KEYCLOAK_DB_VERIFIER
PSQL_ENV="ERP_APP_DB_VERIFIER KEYCLOAK_DB_VERIFIER" psql_owner < "$PILOT_DIR/postgres/bootstrap.sql"
unset ERP_APP_DB_VERIFIER KEYCLOAK_DB_VERIFIER

log "MinIO bucket, users and lifecycle (minio-init.sh)"
dc --profile tools run --rm -T --no-deps minio-init

log "Elasticsearch users, roles and retention (elastic-setup.sh)"
dc --profile tools run --rm -T --no-deps elastic-setup

log "starting Keycloak and the OpenTelemetry Collector"
dc up -d --wait --wait-timeout 600 keycloak otel-collector

log "running the migrator (must succeed before the hosts start)"
dc --profile tools run --rm -T --no-deps migrator

log "starting the web host and the worker ($TAG)"
dc up -d --wait --wait-timeout 300 web worker

log "starting Caddy"
dc up -d --wait --wait-timeout 120 caddy

log "verifying"
failures=0
status="000"
for _ in $(seq 1 24); do
  status="$(web_status /health)"
  [ "$status" = "200" ] && break
  sleep 5
done
if [ "$status" = "200" ]; then log "web /health 200 (readiness: database, key ring)"; else warn "web /health answered $status after 2 minutes"; failures=$((failures + 1)); fi
if [ "$(web_status /alive)" = "200" ]; then log "web /alive 200"; else warn "web /alive did not answer 200"; failures=$((failures + 1)); fi

age=""
for _ in $(seq 1 24); do
  age="$(worker_heartbeat_age)"
  [ -n "$age" ] && [ "$age" -lt 120 ] && break
  sleep 5
done
if [ -n "$age" ] && [ "$age" -lt 120 ]; then log "worker Hangfire heartbeat ${age}s ago"; else warn "no worker heartbeat in the last 2 minutes (age: ${age:-none})"; failures=$((failures + 1)); fi

PLATFORM_HOST="$(env_value PLATFORM_HOST)"
AUTH_HOST="$(env_value AUTH_HOST)"
if curl -fsS -o /dev/null --max-time 20 --resolve "$PLATFORM_HOST:443:127.0.0.1" "https://$PLATFORM_HOST/health"; then
  log "https://$PLATFORM_HOST/health answers through Caddy with a valid certificate"
else
  warn "https://$PLATFORM_HOST/health failed through Caddy: DNS not pointing here yet, or the certificate not issued (docker compose logs caddy)"
  failures=$((failures + 1))
fi
issuer="$(curl -fsS --max-time 20 --resolve "$AUTH_HOST:443:127.0.0.1" "https://$AUTH_HOST/realms/waslabid/.well-known/openid-configuration" 2>/dev/null | jq -r .issuer 2>/dev/null || true)"
if [ "$issuer" = "https://$AUTH_HOST/realms/waslabid" ]; then log "Keycloak issuer $issuer"; else warn "Keycloak issuer is '${issuer:-unreachable}', expected https://$AUTH_HOST/realms/waslabid"; failures=$((failures + 1)); fi

dc ps --format 'table {{.Service}}\t{{.Status}}'

mkdir -p "$STATE_DIR"; chmod 700 "$STATE_DIR"
if [ -f "$STATE_DIR/deployed-tag" ] && [ "$(cat "$STATE_DIR/deployed-tag")" != "$TAG" ]; then
  cp "$STATE_DIR/deployed-tag" "$STATE_DIR/previous-tag"
fi
echo "$TAG" > "$STATE_DIR/deployed-tag"

if [ "$failures" -gt 0 ]; then
  die "$failures check(s) failed; the stack is up with $TAG. Fix and run deploy.sh again (rollback: docs/19 section 8)"
fi
log "deployed $TAG"
