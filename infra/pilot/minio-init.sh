#!/bin/sh
# W-19: one-shot MinIO setup for the pilot, safe to repeat (deploy.sh runs it on every deploy).
#   - the bucket (STORAGE_BUCKET)
#   - waslabid-app: the hosts' user, limited to that bucket (development uses the root user; the pilot never does)
#   - health-probe: list-only on the bucket, for the worker's health check (F-51)
#   - backup-reader: list and read on the bucket, for backup.sh (restore.sh writes as the root user)
#   - the lifecycle rule that expires upload staging after 2 days (vendor spec V-9)
# The root credentials reach mc through MC_HOST_local, not the command line. The three user secrets are passed to
# `mc admin user add` as arguments inside this short-lived container on the internal network only.
set -eu

: "${MINIO_ROOT_USER:?}" "${MINIO_ROOT_PASSWORD:?}" "${STORAGE_BUCKET:?}"
: "${MINIO_APP_ACCESS_KEY:?}" "${MINIO_APP_SECRET_KEY:?}" "${MINIO_HEALTH_PROBE_PASSWORD:?}" "${MINIO_BACKUP_PASSWORD:?}"

export MC_HOST_local="http://${MINIO_ROOT_USER}:${MINIO_ROOT_PASSWORD}@minio:9000"
bucket="$STORAGE_BUCKET"

mc mb --ignore-existing "local/$bucket"

printf '%s' "{\"Version\":\"2012-10-17\",\"Statement\":[
  {\"Effect\":\"Allow\",\"Action\":[\"s3:ListBucket\",\"s3:GetBucketLocation\",\"s3:ListBucketMultipartUploads\"],\"Resource\":[\"arn:aws:s3:::$bucket\"]},
  {\"Effect\":\"Allow\",\"Action\":[\"s3:GetObject\",\"s3:PutObject\",\"s3:DeleteObject\",\"s3:AbortMultipartUpload\",\"s3:ListMultipartUploadParts\"],\"Resource\":[\"arn:aws:s3:::$bucket/*\"]}]}" \
  > /tmp/waslabid-app-policy.json
printf '%s' "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Action\":[\"s3:ListBucket\"],\"Resource\":[\"arn:aws:s3:::$bucket\"]}]}" \
  > /tmp/health-probe-policy.json
printf '%s' "{\"Version\":\"2012-10-17\",\"Statement\":[
  {\"Effect\":\"Allow\",\"Action\":[\"s3:ListBucket\",\"s3:GetBucketLocation\"],\"Resource\":[\"arn:aws:s3:::$bucket\"]},
  {\"Effect\":\"Allow\",\"Action\":[\"s3:GetObject\"],\"Resource\":[\"arn:aws:s3:::$bucket/*\"]}]}" \
  > /tmp/backup-reader-policy.json

mc admin policy create local waslabid-app-rw /tmp/waslabid-app-policy.json
mc admin policy create local health-probe-read /tmp/health-probe-policy.json
mc admin policy create local backup-reader-ro /tmp/backup-reader-policy.json

mc admin user add local "$MINIO_APP_ACCESS_KEY" "$MINIO_APP_SECRET_KEY"
mc admin user add local health-probe "$MINIO_HEALTH_PROBE_PASSWORD"
mc admin user add local backup-reader "$MINIO_BACKUP_PASSWORD"
# attach fails when the policy is already attached, so the result is checked instead of the exit code.
mc admin policy attach local waslabid-app-rw --user "$MINIO_APP_ACCESS_KEY" >/dev/null 2>&1 || true
mc admin policy attach local health-probe-read --user health-probe >/dev/null 2>&1 || true
mc admin policy attach local backup-reader-ro --user backup-reader >/dev/null 2>&1 || true
mc admin user info local "$MINIO_APP_ACCESS_KEY" | grep -q waslabid-app-rw \
  || { echo "waslabid-app has no waslabid-app-rw policy" >&2; exit 1; }
mc admin user info local health-probe | grep -q health-probe-read \
  || { echo "health-probe has no health-probe-read policy" >&2; exit 1; }
mc admin user info local backup-reader | grep -q backup-reader-ro \
  || { echo "backup-reader has no backup-reader-ro policy" >&2; exit 1; }

printf '%s' '{"Rules":[{"ID":"expire-staging","Status":"Enabled","Filter":{"Prefix":"staging/"},"Expiration":{"Days":2}}]}' \
  | mc ilm import "local/$bucket"

echo "bucket ready: $bucket (users waslabid-app, health-probe and backup-reader)"
