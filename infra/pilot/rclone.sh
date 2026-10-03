#!/bin/sh
# W-19: entrypoint of the `rclone` one-shot service (docker-compose.yml). Builds three remotes from the environment and
# runs rclone with the given arguments; no config file is written and no secret reaches the command line.
#   oci:    OCI Object Storage, Jeddah, S3-compatible endpoint, Customer Secret Key (N-01: the bucket is in me-jeddah-1)
#   vault:  rclone crypt over oci:<bucket>/waslabid; file contents and names are encrypted before they leave the VM (N-07)
#   minio:  the pilot's MinIO; backup-reader (read-only) unless RCLONE_MINIO_WRITE=1 (restore.sh), then the root user
# Restoring needs BACKUP_CRYPT_PASSWORD and BACKUP_CRYPT_SALT: keep both in the password manager, off the VM as well.
set -eu

: "${OCI_S3_ENDPOINT:?}" "${OCI_REGION:?}" "${OCI_BACKUP_BUCKET:?}" "${OCI_ACCESS_KEY_ID:?}" "${OCI_SECRET_ACCESS_KEY:?}"
: "${BACKUP_CRYPT_PASSWORD:?}" "${BACKUP_CRYPT_SALT:?}" "${MINIO_BACKUP_PASSWORD:?}"

case "$OCI_S3_ENDPOINT" in
  *.me-jeddah-1.oraclecloud.com|*.me-jeddah-1.oraclecloud.com/) ;;
  *) echo "OCI_S3_ENDPOINT must be the Jeddah (me-jeddah-1) endpoint; refusing to send backups elsewhere (N-01)." >&2; exit 2 ;;
esac

export RCLONE_CONFIG_OCI_TYPE=s3
export RCLONE_CONFIG_OCI_PROVIDER=Other
export RCLONE_CONFIG_OCI_ENDPOINT="$OCI_S3_ENDPOINT"
export RCLONE_CONFIG_OCI_REGION="$OCI_REGION"
export RCLONE_CONFIG_OCI_ACCESS_KEY_ID="$OCI_ACCESS_KEY_ID"
export RCLONE_CONFIG_OCI_SECRET_ACCESS_KEY="$OCI_SECRET_ACCESS_KEY"
export RCLONE_CONFIG_OCI_FORCE_PATH_STYLE=true
# The bucket is created in the console (docs/19 step 4); the key may not list or create buckets.
export RCLONE_CONFIG_OCI_NO_CHECK_BUCKET=true

export RCLONE_CONFIG_VAULT_TYPE=crypt
export RCLONE_CONFIG_VAULT_REMOTE="oci:${OCI_BACKUP_BUCKET}/waslabid"
export RCLONE_CONFIG_VAULT_FILENAME_ENCRYPTION=standard
export RCLONE_CONFIG_VAULT_DIRECTORY_NAME_ENCRYPTION=true
RCLONE_CONFIG_VAULT_PASSWORD="$(printf '%s' "$BACKUP_CRYPT_PASSWORD" | rclone obscure -)"
RCLONE_CONFIG_VAULT_PASSWORD2="$(printf '%s' "$BACKUP_CRYPT_SALT" | rclone obscure -)"
export RCLONE_CONFIG_VAULT_PASSWORD RCLONE_CONFIG_VAULT_PASSWORD2

export RCLONE_CONFIG_MINIO_TYPE=s3
export RCLONE_CONFIG_MINIO_PROVIDER=Minio
export RCLONE_CONFIG_MINIO_ENDPOINT=http://minio:9000
export RCLONE_CONFIG_MINIO_FORCE_PATH_STYLE=true
if [ "${RCLONE_MINIO_WRITE:-0}" = "1" ]; then
  export RCLONE_CONFIG_MINIO_ACCESS_KEY_ID="${MINIO_ROOT_USER:?}"
  export RCLONE_CONFIG_MINIO_SECRET_ACCESS_KEY="${MINIO_ROOT_PASSWORD:?}"
else
  export RCLONE_CONFIG_MINIO_ACCESS_KEY_ID=backup-reader
  export RCLONE_CONFIG_MINIO_SECRET_ACCESS_KEY="$MINIO_BACKUP_PASSWORD"
fi

unset OCI_SECRET_ACCESS_KEY BACKUP_CRYPT_PASSWORD BACKUP_CRYPT_SALT MINIO_BACKUP_PASSWORD MINIO_ROOT_PASSWORD
exec rclone "$@"
