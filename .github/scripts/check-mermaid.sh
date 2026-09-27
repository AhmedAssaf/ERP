#!/usr/bin/env bash
# W-09: renders every Mermaid block in the Markdown documents and fails if any block does not parse.
# Usage (repository root): .github/scripts/check-mermaid.sh
# Needs Node.js; mermaid-cli is fetched by npx at the pinned version below. Works in Git Bash on Windows too.
set -euo pipefail

MMDC_VERSION="${MMDC_VERSION:-12.0.0}"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

# Headless Chrome on GitHub's Ubuntu 24.04 runners cannot use its sandbox (AppArmor), so turn it off for this render.
cat > "$out/puppeteer.json" <<'JSON'
{ "args": ["--no-sandbox", "--disable-setuid-sandbox"] }
JSON

mapfile -t files < <(grep -rl --include='*.md' '^```mermaid' README.md docs | sort)
if [ "${#files[@]}" -eq 0 ]; then
  echo "No Mermaid blocks found."
  exit 0
fi

failed=0
for f in "${files[@]}"; do
  name="$(echo "$f" | tr '/\\' '__')"
  if npx --yes "@mermaid-js/mermaid-cli@${MMDC_VERSION}" --quiet \
      --puppeteerConfigFile "$out/puppeteer.json" \
      --input "$f" --output "$out/$name" > "$out/$name.log" 2>&1; then
    echo "ok     $f"
  else
    echo "FAILED $f"
    sed 's/^/       /' "$out/$name.log"
    failed=1
  fi
done

exit "$failed"
