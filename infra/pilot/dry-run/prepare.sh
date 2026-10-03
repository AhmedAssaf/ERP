#!/usr/bin/env bash
# Local rehearsal ONLY (dry-run/README.md): runs in the runner after generate-secrets.sh and stands in for the manual
# steps of docs/19 step 5 ("nano .env") and step 4 (OCI keys, SMTP credentials). Never run on the VM.
#   1. the .env values a human would type: *.pilot.localhost hosts, Mailpit as the SMTP server with a generated login,
#      the OCI stand-in endpoint and a generated key pair, the backup folder
#   2. a dry-run CA (30 days) and the certificates of Mailpit and the OCI stand-in, signed by it
#   3. dry-run/state/Caddyfile: ../Caddyfile with Caddy's internal issuer (root = the dry-run CA) in the global options
#   4. the runner trusts the dry-run CA, as the VM trusts public CAs
# Safe to repeat: values already set to the dry-run ones and existing certificates are kept.
set -euo pipefail
DRY_DIR="$(cd "$(dirname "$0")" && pwd)"
PILOT_DIR="$(cd "$DRY_DIR/.." && pwd)"
ENV_FILE="$PILOT_DIR/.env"
STATE="$DRY_DIR/state"
CA="$STATE/ca"
[ "$(id -u)" -eq 0 ] || { echo "run as root (in the runner)" >&2; exit 1; }
[ -f "$ENV_FILE" ] || { echo "run generate-secrets.sh first" >&2; exit 1; }
umask 077

# set_env KEY VALUE: replaces KEY's line in .env with builtins only (values never reach another process's arguments).
set_env() {
  local key="$1" value="$2" tmp line found=0
  tmp="$(mktemp "$PILOT_DIR/.env.XXXXXX")"
  while IFS= read -r line || [ -n "$line" ]; do
    if [ "${line%%=*}" = "$key" ] && [ "$found" -eq 0 ]; then
      printf '%s=%s\n' "$key" "$value" >> "$tmp"; found=1
    else
      printf '%s\n' "$line" >> "$tmp"
    fi
  done < "$ENV_FILE"
  [ "$found" -eq 1 ] || printf '%s=%s\n' "$key" "$value" >> "$tmp"
  chmod 600 "$tmp"; mv "$tmp" "$ENV_FILE"
}
current() { grep -m1 -E "^$1=" "$ENV_FILE" | cut -d= -f2- || true; }

echo "1. dry-run values in $ENV_FILE"
set_env PLATFORM_HOST platform.pilot.localhost
set_env AUTH_HOST auth.pilot.localhost
set_env TENANT_BASE_DOMAIN pilot.localhost
set_env KEYCLOAK_TENANT_URL 'https://{slug}.pilot.localhost/'
set_env SMTP_HOST mailpit
set_env SMTP_PORT 587
set_env SMTP_FROM no-reply@pilot.localhost
set_env SMTP_USERNAME dryrun-smtp
[ -n "$(current SMTP_PASSWORD)" ] || set_env SMTP_PASSWORD "$(openssl rand -hex 24)"
set_env OCI_S3_ENDPOINT https://dryrun.compat.objectstorage.me-jeddah-1.oraclecloud.com
set_env OCI_ACCESS_KEY_ID dryrun-backup
[ -n "$(current OCI_SECRET_ACCESS_KEY)" ] || set_env OCI_SECRET_ACCESS_KEY "$(openssl rand -hex 24)"
set_env BACKUP_DIR /var/backups/waslabid-dry

echo "2. dry-run CA and certificates in $CA"
install -d -m 755 "$CA" "$CA/oci"
if [ ! -f "$CA/ca.crt" ]; then
  openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 30 -subj "/CN=WaslaBid dry-run CA" \
    -addext "basicConstraints=critical,CA:true" -addext "keyUsage=critical,keyCertSign,cRLSign" \
    -keyout "$CA/ca.key" -out "$CA/ca.crt" 2>/dev/null
fi
leaf() {
  local name="$1" dns="$2" out="$3" key="$4"
  [ -f "$out" ] && return 0
  openssl req -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -subj "/CN=$dns" -keyout "$key" -out "$STATE/$name.csr" 2>/dev/null
  # The CRL distribution point: MailKit checks revocation (CheckCertificateRevocation, on by default) and refuses a
  # certificate whose status it cannot learn; public certificates (OCI Email Delivery) carry one too.
  printf 'subjectAltName=DNS:%s\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature\nbasicConstraints=critical,CA:false\ncrlDistributionPoints=URI:http://crl/ca.crl\n' "$dns" > "$STATE/$name.ext"
  openssl x509 -req -in "$STATE/$name.csr" -CA "$CA/ca.crt" -CAkey "$CA/ca.key" -CAcreateserial -days 30 \
    -extfile "$STATE/$name.ext" -out "$out" 2>/dev/null
  rm -f "$STATE/$name.csr" "$STATE/$name.ext"
}
leaf mailpit mailpit "$CA/mailpit.crt" "$CA/mailpit.key"
leaf oci dryrun.compat.objectstorage.me-jeddah-1.oraclecloud.com "$CA/oci/public.crt" "$CA/oci/private.key"
# An empty CRL (nothing revoked), served by the `crl` service of the override, renewed on every run.
crlwork="$(mktemp -d)"
touch "$crlwork/index.txt"; echo 1000 > "$crlwork/crlnumber"
printf '[ca]\ndefault_ca=dry\n[dry]\ndatabase=%s/index.txt\ncrlnumber=%s/crlnumber\ndefault_md=sha256\ndefault_crl_days=30\n' \
  "$crlwork" "$crlwork" > "$crlwork/ca.cnf"
install -d -m 755 "$CA/crl"
openssl ca -config "$crlwork/ca.cnf" -gencrl -keyfile "$CA/ca.key" -cert "$CA/ca.crt" -out "$crlwork/crl.pem" 2>/dev/null
openssl crl -in "$crlwork/crl.pem" -outform DER -out "$CA/crl/ca.crl"
rm -rf "$crlwork"
chmod 644 "$CA/crl/ca.crl"
cat /etc/ssl/certs/ca-certificates.crt "$CA/ca.crt" > "$CA/bundle.crt"
# Read by Mailpit, MinIO and the hosts (uid 1654) through bind mounts; dry-run keys only, valid 30 days.
chmod 644 "$CA"/*.crt "$CA/mailpit.key" "$CA/oci/public.crt" "$CA/oci/private.key"

echo "3. $STATE/Caddyfile: internal issuer instead of Let's Encrypt"
awk '
  { print }
  /^\temail \{\$ACME_EMAIL\}$/ {
    print "\t# DRY RUN ONLY (dry-run/prepare.sh): Caddy'"'"'s internal issuer, its root the dry-run CA."
    print "\tlocal_certs"
    print "\tskip_install_trust"
    print "\tpki {"
    print "\t\tca local {"
    print "\t\t\troot {"
    print "\t\t\t\tcert /etc/caddy/dry-ca/ca.crt"
    print "\t\t\t\tkey /etc/caddy/dry-ca/ca.key"
    print "\t\t\t}"
    print "\t\t}"
    print "\t}"
    n++
  }
  END { if (n != 1) exit 1 }' "$PILOT_DIR/Caddyfile" > "$STATE/Caddyfile" \
  || { echo "the Caddyfile has no single 'email {\$ACME_EMAIL}' line to anchor the dry-run issuer" >&2; exit 1; }
chmod 644 "$STATE/Caddyfile"

echo "4. the runner trusts the dry-run CA"
cp "$CA/ca.crt" /usr/local/share/ca-certificates/waslabid-dry-run.crt
update-ca-certificates > /dev/null
echo "prepared"
