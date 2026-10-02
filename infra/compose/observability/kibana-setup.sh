#!/bin/sh
# One-shot Kibana setup (profile kibana): imports the "WaslaBid usage" dashboard and its data view, overwriting, so
# kibana/waslabid-usage.ndjson in the repository is the source of truth (spec 6.7). Idempotent. The credential reaches
# curl through stdin (-K -), never argv; the response is summarised, never echoed whole. Exits non-zero on failure.
set -eu

KIBANA_URL=${KIBANA_URL:-http://kibana:5601}
FILE=/setup/waslabid-usage.ndjson

[ -n "${KIBANA_IMPORT_PASSWORD:-}" ] || { echo "kibana-setup: the import password is not set" >&2; exit 1; }

out=$(mktemp)
status=$(printf 'user = "%s:%s"\n' "$KIBANA_IMPORT_USER" "$KIBANA_IMPORT_PASSWORD" |
  curl -sS -K - -o "$out" -w '%{http_code}' -X POST "$KIBANA_URL/api/saved_objects/_import?overwrite=true" \
    -H 'kbn-xsrf: kibana-setup' -F "file=@$FILE;type=application/ndjson") || status=000

if [ "$status" != 200 ] || ! grep -q '"success":true' "$out"; then
  echo "kibana-setup: import answered $status" >&2
  # Error types and object ids only; the file holds no secret, the response none either.
  grep -o '"\(id\|type\|statusCode\|error\)":"\{0,1\}[^",}]*' "$out" | head -40 >&2 || true
  rm -f "$out"
  exit 1
fi
count=$(grep -o '"successCount":[0-9]*' "$out" | cut -d: -f2)
rm -f "$out"
echo "kibana-setup: imported $count saved objects from waslabid-usage.ndjson"
