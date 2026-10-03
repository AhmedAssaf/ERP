#!/usr/bin/env bash
# W-19: creates infra/pilot/.env from .env.example (if missing) and fills every EMPTY secret with a fresh random value,
# then creates the Data Protection certificate secrets/key-ring.pfx (W-24) if missing. Values are written to the file
# only, never printed (N-10). Re-running it fills only what is still empty, so a value once set is never replaced.
# Non-secret settings (hosts, emails, SMTP user, OCI keys) stay for you to fill by hand.
#
#   sudo infra/pilot/generate-secrets.sh
set -euo pipefail
PILOT_DIR="$(cd "$(dirname "$0")" && pwd)"
ENV_FILE="$PILOT_DIR/.env"
[ "$(id -u)" -eq 0 ] || { echo "run as root (sudo)" >&2; exit 1; }
command -v openssl >/dev/null || { echo "openssl is missing" >&2; exit 1; }

umask 077
if [ ! -f "$ENV_FILE" ]; then
  cp "$PILOT_DIR/.env.example" "$ENV_FILE"
  echo "created $ENV_FILE from .env.example"
fi
chown root:root "$ENV_FILE"; chmod 600 "$ENV_FILE"

hex32() { openssl rand -hex 32; }
b64_32() { openssl rand -base64 32; }

# Secrets generated here; anything else stays manual.
declare -A GEN=(
  [POSTGRES_PASSWORD]=hex32 [ERP_APP_DB_PASSWORD]=hex32 [ERP_KEY_RING_DB_PASSWORD]=hex32 [ERP_WORKER_DB_PASSWORD]=hex32
  [KEYCLOAK_DB_PASSWORD]=hex32 [REDIS_PASSWORD]=hex32 [MINIO_ROOT_PASSWORD]=hex32 [MINIO_APP_SECRET_KEY]=hex32
  [MINIO_HEALTH_PROBE_PASSWORD]=hex32 [MINIO_BACKUP_PASSWORD]=hex32 [KEYCLOAK_ADMIN_PASSWORD]=hex32
  [WASLABID_WEB_CLIENT_SECRET]=hex32 [WASLABID_PLATFORM_CLIENT_SECRET]=hex32 [WASLABID_ADMIN_API_SECRET]=hex32
  [PLATFORM_ADMIN_INITIAL_PASSWORD]=hex32 [VENDORS_CR_AUDIT_KEY]=b64_32 [WASLABID_JOB_SIGNING_KEY]=b64_32
  [KEY_RING_CERT_PASSWORD]=hex32 [ELASTIC_PASSWORD]=hex32 [KIBANA_SYSTEM_PASSWORD]=hex32 [KIBANA_STAFF_PASSWORD]=hex32
  [ELASTIC_MONITOR_PASSWORD]=hex32 [ELASTIC_COLLECTOR_PASSWORD]=hex32 [KIBANA_ENCRYPTION_KEY]=hex32
  [BACKUP_CRYPT_PASSWORD]=hex32 [BACKUP_CRYPT_SALT]=hex32
)

# The file is rewritten line by line with the shell's own read and printf (builtins): a value comes from openssl's
# output and goes only into the new file, never into another process's arguments, where `ps` could see it.
filled=0
tmp="$(mktemp "$PILOT_DIR/.env.XXXXXX")"
# A run that stops half-way must not leave a file of secrets behind (.gitignore also ignores .env.*).
trap 'rm -f "$tmp"' EXIT
while IFS= read -r line || [ -n "$line" ]; do
  key="${line%%=*}"
  if [ "$line" = "$key=" ] && [ -n "${GEN[$key]:-}" ]; then
    value="$(${GEN[$key]})"
    printf '%s=%s\n' "$key" "$value" >> "$tmp"
    filled=$((filled + 1))
  else
    printf '%s\n' "$line" >> "$tmp"
  fi
done < "$ENV_FILE"
unset value
chown root:root "$tmp"; chmod 600 "$tmp"
mv "$tmp" "$ENV_FILE"
echo "filled $filled empty secret(s) in $ENV_FILE"

PFX="$PILOT_DIR/secrets/key-ring.pfx"
if [ ! -f "$PFX" ]; then
  install -d -m 700 "$PILOT_DIR/secrets"
  work="$(mktemp -d)"
  trap 'rm -rf "$work"' EXIT
  pfx_password="$(grep -m1 '^KEY_RING_CERT_PASSWORD=' "$ENV_FILE" | cut -d= -f2-)"
  openssl req -x509 -newkey rsa:3072 -nodes -days 1095 -subj "/CN=waslabid-key-ring" \
    -keyout "$work/k.pem" -out "$work/c.pem" 2>/dev/null
  # The password reaches openssl through a file descriptor, not the command line.
  openssl pkcs12 -export -inkey "$work/k.pem" -in "$work/c.pem" -out "$PFX" -passout fd:3 3<<<"$pfx_password"
  unset pfx_password
  echo "created $PFX (valid 3 years; renewing it makes users sign in again, docs/07 section 4)"
fi
# The hosts run as uid 1654 (the .NET images' app user) and read the certificate through a Compose secret.
chown 1654:1654 "$PFX"; chmod 400 "$PFX"

missing="$(grep -E '^[A-Z_]+=$' "$ENV_FILE" | cut -d= -f1 | grep -vE '^(WATHQ_BASE_URL|WATHQ_API_KEY|KEYCLOAK_OPS_CLIENT_SECRET|WASLABID_JOB_PREVIOUS_SIGNING_KEY)$' || true)"
if [ -n "$missing" ]; then
  echo "still empty, fill by hand:"; printf '%s\n' "$missing" | sed 's/^/  /'
fi
echo "Put a copy of $ENV_FILE and $PFX in the password manager now."
