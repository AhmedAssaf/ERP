# End-to-end scripts (foundation and admin-ui slices)

Playwright scripts driven from the command line, not a `dotnet test` project. They exercise the real local stack
(Keycloak, Mailpit, MinIO, the web host, Docker) through a browser, the way the qa-engineer agent ran them by hand
while proving out the foundation and admin-ui slices (docs/07). They are not part of CI and are not run automatically
by any agent; run them yourself, one at a time, and read the console output (and the screenshots under `shots-admin/`
for the two scenario scripts) to judge the result.

## Prerequisites

1. Node.js 20 or later.
2. The local Compose stack up (`docker compose -f infra/compose/docker-compose.yml up -d` from the repo root) and
   both Keycloak realms current for the admin slice: docs/07 section "Tenant admin host" and "Platform console host"
   describe deleting and reimporting the `waslabid` and `waslabid-platform` realms, then running the migrator with
   `--seed-dev`. Do this once per stack reset, not before every run.
3. `infra/compose/.env` populated (`WASLABID_DEV_USER_PASSWORD` at least; `check.mjs` also reads this file directly).
4. The web host and worker running against that stack (`dotnet run` in `src/Platform.Web` and `src/Platform.Worker`,
   or however you normally start them for manual testing).
5. From this folder: `npm install`.
6. A Chromium build for Playwright to drive. The scripts default to the build Playwright installs under
   `%LOCALAPPDATA%\ms-playwright\chromium-1217\chrome-win64\chrome.exe` (from `npx playwright install chromium` with
   the pinned Playwright version in `package.json`). If yours lands somewhere else, or you are on a different
   Playwright version, set `E2E_CHROMIUM_PATH` to the full path of `chrome.exe` before running any script.

## State and secrets

- `.state/state-admin.json` (git-ignored) holds TOTP seeds captured from Keycloak's enrolment screen and a generated
  password for the throwaway evaluator account `tenant.mjs` creates. It is created on first run. Delete it to force
  fresh TOTP enrolment on the next run (only meaningful right after a realm reset, since Keycloak remembers the
  authenticator otherwise).
- Nothing here prints a secret value: `envValue` in `lib.mjs` reads `infra/compose/.env` and hands the value straight
  to Playwright's form fill, never to `console.log`.
- `shots-admin/` (git-ignored) collects full-page screenshots per step, overwritten on each run.

## Scripts

- **`tenant.mjs`** — the tenant half of the admin-ui scenario: `acme.admin` signs in and enrols TOTP, invites an
  evaluator, the evaluator follows the Mailpit email to set a password and enrol their own TOTP, signs in with the
  invited role only and gets a 403 on `/admin/staff` and `/admin/branding`, then the admin changes the portal colour
  (proving a light colour is stored darkened) and uploads a logo. Run with `node tenant.mjs`. Pass `branding` as the
  first argument (`node tenant.mjs branding`) to skip the invitation flow and only exercise the branding save and logo
  upload, once an evaluator already exists from an earlier run. Results land in `tenant-results.json`.
- **`platform.mjs`** — the platform console half: `platform.admin` signs in with a one-time code, all seven health
  tiles turn healthy, `docker stop erp-clamav` turns the ClamAV tile Unhealthy and sends exactly one alert email, then
  `docker start erp-clamav` turns it healthy again, sends exactly one recovery email, and the incident closes. This
  script stops and restarts a container and waits through several health-check cycles, so it runs for several
  minutes. Run with `node platform.mjs`. Results land in `platform-results.json`.
- **`golden.mjs`** — the admin subset of the W-06 golden screenshot set: `/dev/gallery` at 360px and 1280px, in both
  `ar-SA` and `en-US`, signed in as `acme.admin`. Writes PNGs and an overflow report (`noOverflow` per page) to
  `../Platform.UITests/golden`. Run with `node golden.mjs`.
- **`check.mjs`** — a smaller foundation smoke check: Arabic and RTL rendering on the tenant home page, the language
  switch changing `<html lang>` without a full reload, and that a `beta.admin` token is refused on the `acme` host
  (tenant isolation). Takes the `.env` path and a screenshot output directory as arguments, since it predates the
  shared `lib.mjs` helpers and does not assume this repository's layout:
  `node check.mjs ../../infra/compose/.env ./shots-admin`.

## What "run each script" looks like

None of these scripts assert and exit non-zero on failure; they print `PASS`/`FAIL` per step (`tenant.mjs`,
`platform.mjs`) or a JSON report (`golden.mjs`, `check.mjs`) for you to read. Treat any `FAIL` line, or a `noOverflow:
false`, as a defect to investigate before trusting the slice it covers.
