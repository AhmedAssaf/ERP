# shellcheck shell=bash
# W-19: helpers shared by the pilot scripts (sourced, never run). Every script runs as root on the VM from the
# repository checkout (docs/19). Secret values are read from infra/pilot/.env into variables, passed to containers
# through the environment or stdin, and never echoed (N-10).

PILOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$PILOT_DIR/../.." && pwd)"
ENV_FILE="${ENV_FILE:-$PILOT_DIR/.env}"
STATE_DIR="${STATE_DIR:-/var/lib/waslabid}"
COMPOSE_FILE="$PILOT_DIR/docker-compose.yml"
export PILOT_DIR REPO_ROOT ENV_FILE STATE_DIR COMPOSE_FILE

log()  { printf '%s %s\n' "$(date -u +%H:%M:%SZ)" "$*"; }
warn() { printf '%s WARNING: %s\n' "$(date -u +%H:%M:%SZ)" "$*" >&2; }
die()  { printf '%s ERROR: %s\n' "$(date -u +%H:%M:%SZ)" "$*" >&2; exit 1; }

require_root() {
  [ "$(id -u)" -eq 0 ] || die "run as root (sudo); the scripts drive Docker and write under $STATE_DIR"
}

# The env file holds every pilot secret: it must exist, belong to root and be readable by root alone.
require_env_file() {
  [ -f "$ENV_FILE" ] || die "$ENV_FILE is missing: copy .env.example and fill it (docs/19 step 5)"
  local mode owner
  mode="$(stat -c '%a' "$ENV_FILE")"
  owner="$(stat -c '%U' "$ENV_FILE")"
  [ "$mode" = "600" ] || [ "$mode" = "400" ] || die "$ENV_FILE has mode $mode; run: chmod 600 $ENV_FILE"
  [ "$owner" = "root" ] || die "$ENV_FILE belongs to $owner; run: chown root:root $ENV_FILE"
  if grep -q $'\r' "$ENV_FILE"; then die "$ENV_FILE has Windows line endings; run: sed -i 's/\\r$//' $ENV_FILE"; fi
}

# Value of KEY in the env file (no quotes, no interpolation, as Compose reads it for these keys).
env_value() {
  local line
  line="$(grep -m1 -E "^$1=" "$ENV_FILE" || true)"
  printf '%s' "${line#*=}"
}

# Host list in the form Caddy accepts as site addresses: "a.example.sa, b.example.sa". The value in .env may use
# commas, spaces or both; Caddy refuses "a,b" ("Site addresses cannot contain a comma"), which would take the whole
# site down when Caddy is recreated. Refuses anything that is not a lower-case host name.
normalise_hosts() {
  local raw="$1" host found=()
  for host in $(printf '%s' "$raw" | tr ',' ' ' | tr '[:upper:]' '[:lower:]'); do
    [[ "$host" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$ ]] \
      || { echo "not a host name: '$host'" >&2; return 1; }
    found+=("$host")
  done
  [ "${#found[@]}" -gt 0 ] || { echo "the host list is empty" >&2; return 1; }
  local IFS=,
  printf '%s' "${found[*]}" | sed 's/,/, /g'
}

# Compose with the pilot's file, env file and the image tag deploy.sh chose. TENANT_HOSTS is normalised here, and the
# shell value wins over the env file in Compose's interpolation, so every script and compose.sh hand Caddy a valid list.
dc() {
  local hosts
  hosts="$(normalise_hosts "$(env_value TENANT_HOSTS)")" || die "TENANT_HOSTS in $ENV_FILE is not a list of host names"
  TENANT_HOSTS="$hosts" docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"
}

# The pinned image of one service. (`docker compose config --images <service>` ignores the service and lists them all.)
service_image() {
  dc --profile tools --profile kibana config --format json | jq -r --arg s "$1" '.services[$s].image'
}

# Validates the Caddyfile with the pinned Caddy image and the values Compose would pass, before Caddy is (re)created:
# a broken edge config must never replace a running one.
caddy_validate() {
  local image hosts
  image="$(service_image caddy)"
  hosts="$(normalise_hosts "$(env_value TENANT_HOSTS)")" || return 1
  docker run --rm --network none -v "$PILOT_DIR/Caddyfile:/etc/caddy/Caddyfile:ro" \
    -e ACME_EMAIL="$(env_value ACME_EMAIL)" -e PLATFORM_HOST="$(env_value PLATFORM_HOST)" \
    -e AUTH_HOST="$(env_value AUTH_HOST)" -e TENANT_HOSTS="$hosts" -e ADMIN_ALLOW_CIDR="$(env_value ADMIN_ALLOW_CIDR)" \
    "$image" caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile > /dev/null
}

# Container id of a running service, or nothing.
container_of() {
  dc ps -q "$1" 2>/dev/null | head -n 1
}

# SQL as the owner over the local socket inside the postgres container (trust for local connections in the official
# image). Extra arguments go to psql; SQL comes on stdin. Variables named in PSQL_ENV are passed by name, not value.
psql_owner() {
  local cid db="${PSQL_DB:-platform}" args=()
  cid="$(container_of postgres)"
  [ -n "$cid" ] || die "postgres is not running"
  for name in ${PSQL_ENV:-}; do args+=(-e "$name"); done
  docker exec -i "${args[@]}" "$cid" sh -c 'exec psql -X -q -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$0" "$@"' "$db" "$@"
}

# SCRAM-SHA-256 verifier of the password on stdin (RFC 7677 / PostgreSQL's format), so PostgreSQL never receives the
# password itself, as the migrator does for erp_key_ring and erp_worker (W-24). Printable ASCII passwords only.
scram_verifier() {
  # shellcheck disable=SC2016  # Python source, not shell
  python3 -c '
import base64, hashlib, hmac, os, sys
password = sys.stdin.read().encode()
if not password or any(b < 0x21 or b > 0x7e for b in password):
    sys.exit("password must be printable ASCII without spaces")
salt, iterations = os.urandom(16), 4096
salted = hashlib.pbkdf2_hmac("sha256", password, salt, iterations)
client_key = hmac.new(salted, b"Client Key", "sha256").digest()
server_key = hmac.new(salted, b"Server Key", "sha256").digest()
b64 = lambda b: base64.b64encode(b).decode()
print(f"SCRAM-SHA-256${iterations}:{b64(salt)}${b64(hashlib.sha256(client_key).digest())}:{b64(server_key)}")
'
}

# HTTP status of GET <path> on the web host, asked from inside its own container (no curl in the image).
web_status() {
  local cid
  cid="$(container_of web)"
  [ -n "$cid" ] || { echo 000; return; }
  docker exec "$cid" bash -c "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET $1 HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3 && head -n 1 <&3 | cut -d' ' -f2" 2>/dev/null || echo 000
}

# Seconds since the newest Hangfire server heartbeat (the worker), or empty when there is none.
worker_heartbeat_age() {
  printf '%s\n' "select coalesce(extract(epoch from now() - max(lastheartbeat))::int::text, '') from hangfire.server;" \
    | psql_owner -t -A 2>/dev/null | tr -d '[:space:]'
}
