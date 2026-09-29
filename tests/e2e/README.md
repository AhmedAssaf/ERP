# End-to-end scripts (foundation, admin-ui and vendor slices, W-33)

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
3. `infra/compose/.env` populated (`WASLABID_DEV_USER_PASSWORD`, and `KEYCLOAK_ADMIN`/`KEYCLOAK_ADMIN_PASSWORD` for the
   scripts that create throwaway users; `check.mjs` also reads this file directly). In a git worktree, which has no `.env`
   of its own, set `E2E_ENV_FILE` to the main checkout's file (`E2E_ENV_FILE=/c/Repo/ERP/infra/compose/.env node vendor.mjs`).
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

## Throwaway users (`admin.mjs`)

`vendor.mjs`, `golden.mjs` and `ownership.mjs` do not sign in as the seeded `acme.admin`, `beta.admin` or
`platform.admin`, whose TOTP seeds exist only on the machine that enrolled them. `admin.mjs` creates throwaway staff (a
Keycloak user in the tenant's organization plus an invited `identity.members` row), throwaway platform admins (realm role
`platform-admin`) and vendor-side people through the Keycloak Admin API as the master admin; each enrols its own TOTP on
first sign-in, and `cleanup()` deletes the users, their member rows and their seeds at the end of the run. Company,
dispute, ledger and audit rows stay, as they always have for `vendor.mjs` (append-only). `vendorflow.mjs` holds the
vendor steps the vendor scripts share (PDF, chunked upload, sign in again, company form).

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
  `ar-SA` and `en-US`, signed in as a throwaway acme tenant admin. Writes PNGs and an overflow report (`noOverflow` per page) to
  `../Platform.UITests/golden`, and checks each textarea fits its panel; any overflow sets a non-zero exit code. Run with
  `node golden.mjs`.
- **`vendor.mjs`** — the vendor slice (vendor plan task 7, F-11, F-12, F-10 as narrowed, F-64): a fresh vendor
  (unique email and CR number per run) registers at `acme.localhost:8443/vendor/register`, verifies the email from
  Mailpit, is refused without the privacy notice and then registers the company with it, signs in again, uploads the
  CR certificate (expiry a year ahead) and the VAT certificate (expiry 30 days ago) through the chunked upload with
  ClamAV and sees the VAT one expired, grants and revokes a consent to the seeded test recipient; `acme.admin` approves
  it; `beta.admin` does not see it; the vendor opens `beta.localhost:8443/vendor`, is sent to `/vendor/join`, joins and
  signs in again; `beta.admin` then sees it pending while acme still shows it approved; the last step fails on any
  browser console error (a failed interactive circuit). Needs the realm with self-registration (docs/07 section 4,
  "Vendor slice"), the migrator's `--seed-dev` (the test recipient) and both the web host and the worker. The vendor's
  generated password and the two PDFs live in `.state/`; screenshots in `shots-vendor/`; results in
  `vendor-results.json`. Run with `node vendor.mjs` (about two minutes).
- **`ownership.mjs`** — W-33 (ADR-0013, vendors spec V-15 to V-17): a squatter registers company A and uploads its CR
  certificate; acme's officer sees the ownership check in Arabic and English (right to left, no raw keys, self-declared
  name, verified email), is refused without the box and without a note, and approves with a two-line note after the
  platform admin switched the method to Wathq while it is not set up (the console and the dialog both show the manual
  fallback), then back to Manual; beta's officer approves without the check; five decoys dispute A and one disputes a
  bystander's company B; the real owner is refused at registration with a link to `/vendor/dispute` and raises the sixth
  dispute on A (flagged over the cap, not refused); the console lists them grouped by company, oldest first; the platform
  admin accepts it for review, upholds it and rejects B's; the database and Keycloak show the move; the owner reaches
  `/vendor` on acme and beta without `/vendor/join`; the squatter's open session and a fresh sign-in get 403; one alert
  email without personal data. Needs the web host and the worker (the alert job runs every five minutes, so the run takes
  about eight minutes). Screenshots in `shots-ownership/`, results in `ownership-results.json`, non-zero exit on any FAIL.
  If a run fails before resolving its disputes, `node ownership.mjs sweep` rejects them in the console.
- **`check.mjs`** — a smaller foundation smoke check: Arabic and RTL rendering on the tenant home page, the language
  switch changing `<html lang>` without a full reload, and that a `beta.admin` token is refused on the `acme` host
  (tenant isolation). Takes the `.env` path and a screenshot output directory as arguments, since it predates the
  shared `lib.mjs` helpers and does not assume this repository's layout:
  `node check.mjs ../../infra/compose/.env ./shots-admin`.

## What "run each script" looks like

None of these scripts assert and exit non-zero on failure; they print `PASS`/`FAIL` per step (`tenant.mjs`,
`platform.mjs`) or a JSON report (`golden.mjs`, `check.mjs`) for you to read. Treat any `FAIL` line, or a `noOverflow:
false`, as a defect to investigate before trusting the slice it covers.
