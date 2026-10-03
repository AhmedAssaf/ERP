#!/usr/bin/env bash
# Verifies the cosign signature of every docker.elastic.co image pinned in infra/compose/docker-compose.yml
# against Elastic's published public key (https://artifacts.elastic.co/cosign.pub, committed as
# infra/security/elastic-cosign.pub). Fails on a missing or invalid signature, or on an Elastic image that is
# not pinned by digest.
# Verified locally (2026-10-03) with cosign v2.5.0 and exactly `cosign verify --key <key> <image@digest>`: no
# --insecure-ignore-tlog was needed, because Elastic's signatures are recorded in the Rekor transparency log.
# Run locally: install cosign v2.5.0 and run `bash .github/scripts/verify-elastic-signatures.sh` from the
# repository root.
set -euo pipefail
key=infra/security/elastic-cosign.pub
images=$(grep -E '^\s+image:\s+docker\.elastic\.co/' infra/compose/docker-compose.yml | awk '{print $2}' | sort -u)
test -n "$images" || { echo "no docker.elastic.co images found"; exit 1; }
for image in $images; do
  case "$image" in
    *@sha256:*) ;;
    *) echo "::error::$image is not pinned by digest"; exit 1 ;;
  esac
  echo "::group::$image"
  cosign verify --key "$key" "$image" > /dev/null
  echo "verified $image"
  echo "::endgroup::"
done
