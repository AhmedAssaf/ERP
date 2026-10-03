#!/usr/bin/env bash
# One-shot Elasticsearch setup for the W-10 telemetry store (spec O-4, O-12, O-13, O-18; F-53). Runs as the built-in
# elastic user after Elasticsearch is healthy, on every `docker compose up -d`; every step is a create-or-replace, so a
# second run changes nothing. It prints no password (N-10): credentials reach curl through stdin (-K -), never argv,
# and request bodies holding a password are never echoed. Any failed step exits non-zero, which keeps the collector
# from starting (depends_on: service_completed_successfully).
set -euo pipefail

ES_URL=${ES_URL:-http://elasticsearch:9200}

fail() { echo "elastic-setup: $*" >&2; exit 1; }

# A value must be present and safe to place inside a JSON string literal (generated values are hex).
require() {
  local name=$1 value=${!1:-}
  [[ -n $value ]] || fail "$name is not set in infra/compose/.env"
  [[ $value != *[\"\\]* && $value != *$'\n'* ]] || fail "$name must not contain a quote, a backslash or a newline"
}
require_password() {
  require "$1"
  local value=${!1}
  [[ ${#value} -ge 16 ]] || fail "$1 must be at least 16 characters"
}
require_age() {
  require "$1"
  [[ ${!1} =~ ^[1-9][0-9]*d$ ]] || fail "$1 must be a number of days such as 3d or 30d"
}

for p in ELASTIC_PASSWORD KIBANA_SYSTEM_PASSWORD KIBANA_STAFF_PASSWORD ELASTIC_MONITOR_PASSWORD ELASTIC_COLLECTOR_PASSWORD; do
  require_password "$p"
done
require KIBANA_STAFF_USER
[[ $KIBANA_STAFF_USER =~ ^[a-z][a-z0-9_.-]{2,63}$ ]] || fail "KIBANA_STAFF_USER must be 3 to 64 lowercase letters, digits, '_', '.', '-'"
case $KIBANA_STAFF_USER in
  elastic|kibana|kibana_system|logstash_system|beats_system|apm_system|remote_monitoring_user|waslabid_*)
    fail "KIBANA_STAFF_USER must not be a built-in or service user name" ;;
esac
for a in TELEMETRY_LOGS_RETENTION TELEMETRY_TRACES_RETENTION TELEMETRY_METRICS_RETENTION; do
  require_age "$a"
done

# es_req METHOD PATH [BODY]: sends the request as elastic and leaves the status in REQ_STATUS and the response body in
# the file REQ_OUT. The credential reaches curl through stdin; the request body is never echoed.
REQ_OUT=$(mktemp)
trap 'rm -f "$REQ_OUT"' EXIT
REQ_STATUS=000
es_req() {
  local method=$1 path=$2 body=${3:-}
  if [[ -n $body ]]; then
    REQ_STATUS=$(printf 'user = "elastic:%s"\n' "$ELASTIC_PASSWORD" |
      curl -sS -K - -o "$REQ_OUT" -w '%{http_code}' -X "$method" "$ES_URL$path" \
        -H 'Content-Type: application/json' --data-binary @<(printf '%s' "$body")) || REQ_STATUS=000
  else
    REQ_STATUS=$(printf 'user = "elastic:%s"\n' "$ELASTIC_PASSWORD" |
      curl -sS -K - -o "$REQ_OUT" -w '%{http_code}' -X "$method" "$ES_URL$path") || REQ_STATUS=000
  fi
}

# es METHOD PATH [BODY]: es_req that fails on any status other than 2xx, printing only the status and Elasticsearch's
# error type.
es() {
  es_req "$@"
  if [[ $REQ_STATUS != 2* ]]; then
    fail "$1 $2 answered $REQ_STATUS $(grep -o '"type":"[^"]*"' "$REQ_OUT" | head -1 || true)"
  fi
}

step() { echo "elastic-setup: $*"; }

# Wait until the security index answers for the elastic user (the node can be up before it is ready).
for i in $(seq 1 30); do
  es_req GET /_security/_authenticate
  [[ $REQ_STATUS == 200 ]] && break
  [[ $i -lt 30 ]] || fail "elastic user cannot authenticate (is ELASTIC_PASSWORD the one this data volume was created with?)"
  sleep 2
done

# (1) Kibana's own service account.
step "kibana_system password"
es POST /_security/user/kibana_system/_password "{\"password\":\"$KIBANA_SYSTEM_PASSWORD\"}"

# (2) Roles and users. Least privilege: the collector only creates documents in the three telemetry families; the
# monitor only reads cluster health (the worker's Telemetry check, F-60); the errors reader only reads logs (F-53's
# ES|QL summary, its credential is F-53's). The staff user has the built-in viewer role (spec 6.7, O-18).
step "role waslabid_collector_writer"
es PUT /_security/role/waslabid_collector_writer '{
  "cluster": [],
  "indices": [
    { "names": ["logs-*", "traces-*", "metrics-*"], "privileges": ["auto_configure", "create_doc"] }
  ],
  "metadata": { "owner": "waslabid", "purpose": "OpenTelemetry Collector elasticsearch exporter" }
}'
step "role waslabid_monitor"
es PUT /_security/role/waslabid_monitor '{
  "cluster": ["monitor"],
  "indices": [],
  "metadata": { "owner": "waslabid", "purpose": "worker Telemetry check, _cluster/health (F-60)" }
}'
step "role waslabid_errors_reader"
es PUT /_security/role/waslabid_errors_reader '{
  "cluster": [],
  "indices": [
    { "names": ["logs-*"], "privileges": ["read", "view_index_metadata"] }
  ],
  "metadata": { "owner": "waslabid", "purpose": "F-53 error summary, ES|QL on logs only" }
}'

step "user waslabid_collector"
es POST /_security/user/waslabid_collector "{\"password\":\"$ELASTIC_COLLECTOR_PASSWORD\",\"roles\":[\"waslabid_collector_writer\"],\"full_name\":\"WaslaBid OpenTelemetry Collector\"}"
step "user waslabid_monitor"
es POST /_security/user/waslabid_monitor "{\"password\":\"$ELASTIC_MONITOR_PASSWORD\",\"roles\":[\"waslabid_monitor\"],\"full_name\":\"WaslaBid Telemetry check\"}"
step "user $KIBANA_STAFF_USER (viewer)"
es POST "/_security/user/$KIBANA_STAFF_USER" "{\"password\":\"$KIBANA_STAFF_PASSWORD\",\"roles\":[\"viewer\"],\"full_name\":\"WaslaBid platform staff\",\"metadata\":{\"owner\":\"waslabid\",\"role\":\"staff\"}}"

# Offboarding: a staff user this script created earlier (metadata owner waslabid, role staff) under another name is
# deleted, so renaming KIBANA_STAFF_USER never leaves the old account active. Built-in and other users are untouched.
step "staff users no longer configured"
es GET "/_security/user?filter_path=*.metadata.role"
for old in $(grep -o '"[a-z][a-z0-9_.-]*":{"metadata":{"role":"staff"}}' "$REQ_OUT" | cut -d'"' -f2); do
  [[ $old == "$KIBANA_STAFF_USER" ]] && continue
  es GET "/_security/user/$old?filter_path=*.metadata.owner"
  grep -q '"owner":"waslabid"' "$REQ_OUT" || continue
  step "delete staff user $old (not KIBANA_STAFF_USER any more)"
  es DELETE "/_security/user/$old"
done

# (3) Retention (O-12). Elasticsearch 9's built-in OTel index templates (logs-otel@template, traces-otel@template,
# metrics-otel@template and the hidden metrics-*.otel aggregates) manage their data streams with index lifecycle
# policies (index.lifecycle.name from logs@settings, traces@settings, metrics@tsdb-settings), not with data stream
# lifecycle, and compose the optional component templates logs-otel@custom, traces-otel@custom and
# metrics-otel@custom after them. Those three name our policies and set 0 replicas (single node). A policy is updated
# in place, so a changed retention applies to existing backing indices too. Rollover is daily, so an index is deleted
# between the retention age and one day after it.
policy() {
  local name=$1 age=$2
  es PUT "/_ilm/policy/$name" "{
    \"policy\": {
      \"_meta\": { \"owner\": \"waslabid\", \"retention\": \"$age\" },
      \"phases\": {
        \"hot\": { \"actions\": { \"rollover\": { \"max_age\": \"1d\", \"max_primary_shard_size\": \"10gb\" } } },
        \"delete\": { \"min_age\": \"$age\", \"actions\": { \"delete\": {} } }
      }
    }
  }"
}
# ignore_malformed (logs and traces) is pinned here although the built-in otel templates set it: a masked number-typed value
# (NumericRedaction, spec 7.1) is kept in _source and the field listed in _ignored instead of the record being refused,
# and a future change of the built-in template cannot remove it (these components are composed last).
custom() {
  local component=$1 name=$2 mappings=${3:-} malformed=${4:-}
  es PUT "/_component_template/$component" "{
    \"template\": {
      \"settings\": { \"index\": { \"lifecycle\": { \"name\": \"$name\" }, \"number_of_replicas\": 0${malformed:+, \"mapping\": { \"ignore_malformed\": true \}} } }${mappings:+,
      \"mappings\": $mappings}
    },
    \"_meta\": { \"owner\": \"waslabid\", \"managed_by\": \"infra/compose/observability/elastic-setup.sh\" }
  }"
}

# The usage metrics of spec 6.1, mapped ahead of the first data point, as the otel mapping would map them (gauges as
# long under metrics.*, tags as keyword dimensions under attributes.*). Without them the "WaslaBid usage" dashboard's
# panels and controls report "field not found" until the app has run, instead of "No results" (task 8 acceptance).
USAGE_MAPPINGS='{
  "properties": {
    "metrics": {
      "type": "passthrough", "priority": 10, "dynamic": true,
      "properties": {
        "waslabid.circuits.connected":         { "type": "long", "time_series_metric": "gauge" },
        "waslabid.users.concurrent":           { "type": "long", "time_series_metric": "gauge" },
        "waslabid.users.active":               { "type": "long", "time_series_metric": "gauge" },
        "waslabid.users.active.all_tenants":   { "type": "long", "time_series_metric": "gauge" },
        "waslabid.tenders":                    { "type": "long", "time_series_metric": "gauge" },
        "waslabid.opportunities":              { "type": "long", "time_series_metric": "gauge" }
      }
    },
    "attributes": {
      "type": "passthrough", "priority": 20, "dynamic": true, "time_series_dimension": true,
      "properties": {
        "waslabid.tenant.slug":       { "type": "keyword", "time_series_dimension": true },
        "waslabid.user.kind":         { "type": "keyword", "time_series_dimension": true },
        "waslabid.window":            { "type": "keyword", "time_series_dimension": true },
        "waslabid.tender.state":      { "type": "keyword", "time_series_dimension": true },
        "waslabid.tender.visibility": { "type": "keyword", "time_series_dimension": true },
        "waslabid.directory":         { "type": "keyword", "time_series_dimension": true }
      }
    }
  }
}'

# The data stream the app's metrics land in: the exporter's dynamic routing gives the .NET meters (no data_stream.*
# attributes, no receiver scope) the dataset generic.otel. Created empty if missing, and the mappings above added to
# its write index if it predates them; both are no-ops on a second run.
ensure_usage_stream() {
  local ds=metrics-generic.otel-default
  es_req PUT "/_data_stream/$ds"
  if [[ $REQ_STATUS != 2* ]] && ! grep -q resource_already_exists_exception "$REQ_OUT"; then
    fail "PUT /_data_stream/$ds answered $REQ_STATUS"
  fi
  es PUT "/$ds/_mapping?write_index_only=true" "$USAGE_MAPPINGS"
}
step "lifecycle waslabid-logs ($TELEMETRY_LOGS_RETENTION) via logs-otel@custom"
policy waslabid-logs "$TELEMETRY_LOGS_RETENTION"
custom logs-otel@custom waslabid-logs "" ignore_malformed
step "lifecycle waslabid-traces ($TELEMETRY_TRACES_RETENTION) via traces-otel@custom"
policy waslabid-traces "$TELEMETRY_TRACES_RETENTION"
custom traces-otel@custom waslabid-traces "" ignore_malformed
step "lifecycle waslabid-metrics ($TELEMETRY_METRICS_RETENTION) via metrics-otel@custom"
policy waslabid-metrics "$TELEMETRY_METRICS_RETENTION"
custom metrics-otel@custom waslabid-metrics "$USAGE_MAPPINGS"
step "usage metric mappings on metrics-generic.otel-default"
ensure_usage_stream

step "done"
