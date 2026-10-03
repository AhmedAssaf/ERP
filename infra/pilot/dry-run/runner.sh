#!/usr/bin/env bash
# Local rehearsal ONLY (dry-run/README.md): starts the "VM" container on a laptop with Docker Desktop and runs commands
# in it. From the repository root, in Git Bash:
#   infra/pilot/dry-run/runner.sh start      # build the runner image, start it, clone the repository into it
#   infra/pilot/dry-run/runner.sh sync       # copy the working tree's infra/pilot (uncommitted edits too) into the clone
#   infra/pilot/dry-run/runner.sh exec <command...>   # e.g. exec infra/pilot/deploy.sh --tag <tag>
#   infra/pilot/dry-run/runner.sh rm         # remove the runner and its folders (after `compose down -v`)
#
# Why a container: the scripts expect root, a Linux file system (modes, owners) and paths that the Docker engine sees as
# the scripts do (Compose bind mounts). The clone lives at /opt/waslabid-dry inside Docker Desktop's VM, mounted at the
# same path in the runner, so every bind mount resolves. The runner uses the host network of that VM, where the
# published ports (Caddy 8443, Keycloak 127.0.0.1:8080, Mailpit 587) answer on 127.0.0.1 as on the pilot VM.
set -euo pipefail
cd "$(dirname "$0")/../../.."
export MSYS_NO_PATHCONV=1
NAME=waslabid-dry-runner
IMAGE=waslabid-dry/runner:local
CLONE=/opt/waslabid-dry/repo
# The main repository's .git (this checkout may be a worktree, whose .git file holds a Windows path).
GIT_COMMON="$(cd "$(git rev-parse --git-common-dir)" && { pwd -W 2>/dev/null || pwd; })"
BRANCH="$(git rev-parse --abbrev-ref HEAD)"
WORKTREE="$(pwd -W 2>/dev/null || pwd)"

case "${1:-}" in
  start)
    docker build -q -t "$IMAGE" -f infra/pilot/dry-run/runner.Dockerfile infra/pilot/dry-run > /dev/null
    docker rm -f "$NAME" > /dev/null 2>&1 || true
    docker run -d --name "$NAME" --network host --add-host mailpit:127.0.0.1 \
      -v /var/run/docker.sock:/var/run/docker.sock \
      -v /opt/waslabid-dry:/opt/waslabid-dry \
      -v /var/lib/waslabid-dry:/var/lib/waslabid \
      -v /var/backups/waslabid-dry:/var/backups/waslabid-dry \
      -v "$WORKTREE:/src:ro" -v "$GIT_COMMON:/srcgit:ro" \
      -e PILOT_COMPOSE_OVERRIDE="$CLONE/infra/pilot/dry-run/compose.override.yml" \
      -e PILOT_HTTPS_PORT=8443 \
      -w "$CLONE" "$IMAGE" > /dev/null
    docker exec "$NAME" bash -c "git config --global --add safe.directory '*' \
      && { [ -d $CLONE/.git ] || git clone -q --no-local -b '$BRANCH' /srcgit $CLONE; } \
      && git -C $CLONE fetch -q /srcgit '$BRANCH' && git -C $CLONE checkout -q -B '$BRANCH' FETCH_HEAD \
      && git -C $CLONE log --oneline -1"
    "$0" sync
    ;;
  sync)
    # Only infra/pilot: what the rehearsal changes. Secrets and dry-run state stay where they are.
    docker exec "$NAME" rsync -rt --exclude .gitattributes --exclude .env --exclude '.env.*' --exclude secrets/ --exclude dry-run/state/ \
      /src/infra/pilot/ "$CLONE/infra/pilot/"
    docker exec "$NAME" bash -c "chmod 755 $CLONE/infra/pilot/*.sh $CLONE/infra/pilot/dry-run/*.sh"
    ;;
  exec)
    shift
    docker exec -i "$NAME" "$@"
    ;;
  rm)
    docker rm -f "$NAME" > /dev/null 2>&1 || true
    docker run --rm -v /opt:/o -v /var/lib:/l -v /var/backups:/b alpine:3.20 \
      rm -rf /o/waslabid-dry /l/waslabid-dry /b/waslabid-dry
    ;;
  *)
    sed -n '2,13p' "$0"; exit 2 ;;
esac
