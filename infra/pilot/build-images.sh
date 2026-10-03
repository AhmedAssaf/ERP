#!/usr/bin/env bash
# W-19: builds the web, worker and migrator images for linux/arm64 on a BUILD machine (an amd64 laptop with Docker
# Desktop, or Linux with buildx) and streams them to the pilot VM over SSH. No registry is involved: the images go
# straight from this machine to the VM in Jeddah.
#
#   infra/pilot/build-images.sh                       # build only, tag = current commit
#   infra/pilot/build-images.sh <ssh target>          # build and load on the VM, e.g. ubuntu@pilot.example.sa
#
# The tag is the commit's 12-character short hash; the working tree must be clean so the tag names exactly what was
# built. Run with no argument on the VM itself to build natively there (docs/19 step 6). Then on the VM: git checkout <tag> && sudo infra/pilot/deploy.sh --tag <tag>
set -euo pipefail

cd "$(dirname "$0")/../.."
TARGET="${1:-}"
PLATFORM="${PLATFORM:-linux/arm64}"
PREFIX="${IMAGE_PREFIX:-waslabid}"

if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
  echo "The working tree has uncommitted changes; commit or stash them so the tag matches the build." >&2
  exit 1
fi
TAG="$(git rev-parse --short=12 HEAD)"

for target in web worker migrator; do
  echo "== building $PREFIX/$target:$TAG for $PLATFORM"
  docker buildx build --platform "$PLATFORM" -f infra/pilot/docker/app.Dockerfile --target "$target" \
    --label "org.opencontainers.image.revision=$(git rev-parse HEAD)" \
    -t "$PREFIX/$target:$TAG" --load .
done

if [ -z "$TARGET" ]; then
  echo "Built $PREFIX/{web,worker,migrator}:$TAG. Ship with: $0 <ssh target>"
  exit 0
fi

echo "== loading the images on $TARGET (about 270 MB each before compression)"
docker save "$PREFIX/web:$TAG" "$PREFIX/worker:$TAG" "$PREFIX/migrator:$TAG" | gzip -1 | ssh "$TARGET" 'gunzip | sudo docker load'
echo "Loaded. On the VM: cd /opt/waslabid && git fetch && git checkout $TAG && sudo infra/pilot/deploy.sh --tag $TAG"
