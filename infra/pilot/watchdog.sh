#!/usr/bin/env bash
# W-19: dead-man's switch for what the worker cannot report about itself. Every F-60 alert is sent BY the worker, so a
# stopped worker, a stopped Docker or a missed backup would otherwise be silent. Run every five minutes by
# waslabid-watchdog.timer (systemd/). One email when a check fails and one when it recovers, straight to OCI Email
# Delivery (curl, STARTTLS) to ALERT_RECIPIENT; failures are also written to the journal (journalctl -u waslabid-watchdog).
#
# Checks: the worker's Hangfire heartbeat (younger than 3 minutes), web /health (readiness), the last backup (younger
# than 26 hours), the root file system (below 85 %), and a pending reboot older than 7 days (security updates).
# A whole-VM outage cannot be seen from inside the VM: docs/19 section 6 sets up an OCI Monitoring alarm for that.
set -uo pipefail
# shellcheck source=infra/pilot/lib.sh
. "$(dirname "$0")/lib.sh"

require_root
# Installed by bootstrap.sh before the first deploy: nothing to do until then.
[ -f "$STATE_DIR/deployed-tag" ] || { echo "not deployed yet; nothing to do"; exit 0; }
require_env_file
IMAGE_TAG="$(cat "$STATE_DIR/deployed-tag" 2>/dev/null || echo unknown)"
export IMAGE_TAG
WATCH_DIR="$STATE_DIR/watchdog"
mkdir -p "$WATCH_DIR"

RECIPIENT="$(env_value ALERT_RECIPIENT)"
FROM="$(env_value SMTP_FROM)"
PLATFORM_HOST="$(env_value PLATFORM_HOST)"

# Straight to OCI Email Delivery from the VM (STARTTLS on port 587). The login goes to curl on stdin, never on its
# command line, so it is not visible in the process list or the journal (N-10).
send_mail() {
  local subject="$1" body="$2" host port user password
  host="$(env_value SMTP_HOST)"; port="$(env_value SMTP_PORT)"
  user="$(env_value SMTP_USERNAME)"; password="$(env_value SMTP_PASSWORD)"
  if [ -z "$host" ] || [ -z "$user" ] || [ -z "$password" ]; then
    echo "watchdog: cannot send '$subject': SMTP_HOST, SMTP_USERNAME or SMTP_PASSWORD is empty in .env" >&2
    return 1
  fi
  printf 'From: %s
To: %s
Subject: %s
Content-Type: text/plain; charset=utf-8

%s

VM: %s
Time (UTC): %s
'     "$FROM" "$RECIPIENT" "$subject" "$body" "$(hostname)" "$(date -u '+%Y-%m-%d %H:%M')" > "$WATCH_DIR/mail.tmp"
  printf 'user = "%s:%s"
' "$user" "$password"     | curl -sS --fail --max-time 30 --ssl-reqd -K - "smtp://${host}:${port:-587}"         --mail-from "$FROM" --mail-rcpt "$RECIPIENT" -T "$WATCH_DIR/mail.tmp" >/dev/null
  local rc=$?
  rm -f "$WATCH_DIR/mail.tmp"
  return $rc
}

# report <check> <ok 0|1> <message>
report() {
  local check="$1" ok="$2" message="$3" marker="$WATCH_DIR/$1.down"
  if [ "$ok" -eq 1 ]; then
    if [ -f "$marker" ]; then
      send_mail "[WaslaBid pilot] $check has recovered" "$check is healthy again. $message" && rm -f "$marker"
    fi
  else
    echo "watchdog: $check: $message" >&2
    if [ ! -f "$marker" ]; then
      send_mail "[WaslaBid pilot] $check is down" "$message
Platform console: https://$PLATFORM_HOST/platform
Runbook: docs/19-pilot-runbook.md section 6." && date -u +%s > "$marker"
    fi
  fi
}

if ! docker info >/dev/null 2>&1; then
  # Without Docker the stack is down: the journal is all that is left (and the OCI alarm, docs/19 section 6).
  echo "watchdog: Docker is not answering" >&2
  exit 1
fi

age="$(worker_heartbeat_age)"
if [ -n "$age" ] && [ "$age" -lt 180 ]; then
  report worker 1 "Hangfire heartbeat ${age}s ago."
else
  report worker 0 "The worker's Hangfire heartbeat is ${age:-missing}s old: no background job, health check or F-60 alert runs. Check: docker compose ... logs worker."
fi

status="$(web_status /health)"
if [ "$status" = "200" ]; then
  report web 1 "/health answers 200."
else
  report web 0 "The web host's /health answers $status (503: the database or the key ring is not reachable)."
fi

last="$(cat "$STATE_DIR/last-backup" 2>/dev/null || echo 0)"
hours=$(( ($(date -u +%s) - last) / 3600 ))
if [ "$hours" -lt 26 ]; then
  report backup 1 "Last backup ${hours}h ago."
else
  report backup 0 "The last successful backup is ${hours}h old (N-07). Check: journalctl -u waslabid-backup."
fi

used="$(df --output=pcent / | tail -n 1 | tr -dc '0-9')"
if [ "$used" -lt 85 ]; then
  report disk 1 "Root file system ${used}% used."
else
  report disk 0 "The root file system is ${used}% full (PostgreSQL, MinIO and Elasticsearch live on it)."
fi

if [ -f /var/run/reboot-required ] && [ -n "$(find /var/run/reboot-required -mmin +10080 2>/dev/null)" ]; then
  report reboot 0 "Security updates have waited more than 7 days for a reboot (docs/19 section 8, upgrades)."
else
  report reboot 1 "No reboot pending for more than 7 days."
fi
exit 0
