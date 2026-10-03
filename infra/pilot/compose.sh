#!/usr/bin/env bash
# W-19: docker compose with the pilot's file, env file and deployed image tag, for everyday operations on the VM.
#   sudo infra/pilot/compose.sh ps
#   sudo infra/pilot/compose.sh logs --tail 100 caddy
#   sudo infra/pilot/compose.sh --profile kibana up -d kibana kibana-setup     # Kibana on demand (O-17, O-18)
#   sudo infra/pilot/compose.sh stop kibana
# Deploys go through deploy.sh, never `compose.sh up` on the hosts: it would skip the migrator.
set -euo pipefail
# shellcheck source=infra/pilot/lib.sh
. "$(dirname "$0")/lib.sh"
require_root
require_env_file
IMAGE_TAG="$(cat "$STATE_DIR/deployed-tag" 2>/dev/null || echo none)"
export IMAGE_TAG
dc "$@"
