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
   scripts that create throwaway users; `check.mjs` also reads this file directly).
   In a git worktree, which starts without the git-ignored `.env`, set `E2E_ENV_FILE` to the full path of the one in
   use (for example the main checkout's) rather than copying it. When the branch runs against a database of its own on
   the same PostgreSQL (hosts started with a `--ConnectionStrings:Platform=...` override), set `E2E_DB` to its name so
   the scripts' test-data SQL (`admin.mjs`, `revalidation.mjs`) writes and reads there instead of `platform`.
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
  ClamAV and sees the VAT one expired, grants and revokes a consent to the seeded test recipient; acme's admin approves
  it; beta's admin does not see it; the vendor opens `beta.localhost:8443/vendor`, is sent to `/vendor/join`, joins and
  signs in again; beta's admin then sees it pending while acme still shows it approved; a step fails on any browser
  console error (a failed interactive circuit); and both of the vendor's sign-outs must post `id_token_hint` to Keycloak
  in a form body, with no logout confirmation and no requested or navigated URL carrying the hint (W-21). The two
  admins are throwaway tenant admins with their own TOTP seeds (`admin.mjs`), deleted with their member rows at the
  end. Needs the realm with self-registration (docs/07 section 4,
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
- **`admin.mjs`** — shared fixtures, not a script: the Keycloak Admin API as the master admin, test-data SQL as the
  Compose superuser, and throwaway staff users with their own TOTP seeds, removed by `cleanup()`. `E2E_DB` names another database
  (see above).
- **`revalidation.mjs`** — W-21 through Caddy with real SignalR circuits (docs/09 W-21, docs/03 diagram 5). Creates its
  own users through the Keycloak Admin API (the seeded admins are not touched): two tenant admins of acme hold
  `/admin/staff` open, one is removed from the acme organization and the other's account disabled at the same moment,
  and without touching the pages the script measures the seconds until each reloads into a new document, where it
  lands (the removed admin on the access-removed page, whose Sign out must end the Keycloak session and reach the
  sign-in page; the disabled one on the sign-in page), how many navigation requests the tab makes (more than 5 is a reload loop), that the session's original cookie
  is challenged on a fresh request, and the `identity.session_revoked` row. A vendor registered on acme and joined to beta
  holds `/vendor` open on both hosts and is removed from acme: the acme tab must reload, `/vendor/join` must refuse it in
  Arabic (right to left) and English with no raw resource key while the membership stays absent
  (`vendor.membership_restore_refused`), and the beta tab must keep its circuit. A control tenant admin with no page
  open is removed afterwards and navigates afresh every 10 seconds, and must land on the access-removed page and sign
  out from it to the sign-in page. Access is then restored, both admins sign in again,
  the throwaway staff users are deleted and the vendor is put back in acme. Every sign-out of the run (the vendor's
  two and the two from the access-removed page) must post `id_token_hint` to Keycloak in a form body, and no URL any
  watched page requested or navigated to may carry it. Needs the Admin API credentials
  (`KEYCLOAK_ADMIN`, `KEYCLOAK_ADMIN_PASSWORD`) in `.env` and the `erp-postgres` container (member rows and audit reads
  as the Compose user). Screenshots in `shots-revalidation/`, results in `revalidation-results.json`. Run with
  `node revalidation.mjs` (about ten minutes); it exits non-zero when a step fails.
- **`check.mjs`** — a smaller foundation smoke check: Arabic and RTL rendering on the tenant home page, the language
  switch changing `<html lang>` without a full reload, and that a `beta.admin` token is refused on the `acme` host
  (tenant isolation). Takes the `.env` path and a screenshot output directory as arguments, since it predates the
  shared `lib.mjs` helpers and does not assume this repository's layout:
  `node check.mjs ../../infra/compose/.env ./shots-admin`.
- **`keyring.mjs`** — W-24, the shared Data Protection key ring (docs/07 section 4, "Edge and Data Protection key
  ring"). A throwaway acme tenant admin (Keycloak Admin API, member row as the dev seed makes it) signs in through
  Caddy; a new browser context with its cookies must open `/admin/staff` with no request to Keycloak and an
  interactive circuit; the cookie is accepted when replayed and a tampered copy is sent to Keycloak; the key rows stay
  the ones seen at sign-in. The default run (`node keyring.mjs`, about 15 seconds) then starts a second web instance
  itself on port 5274 from the built `Platform.Web.dll`, replays the cookie there with the headers Caddy sends (and
  once with an `X-Forwarded-Host` naming beta, which must be ignored), restarts it, replays again, checks its log holds
  no key material and no new key, stops it and deletes the throwaway user. To prove a restart of the host behind
  Caddy: `node keyring.mjs signin`, restart `dotnet run --project src/Platform.Web`, `node keyring.mjs check`
  (`--direct <url>` adds any other instance), `node keyring.mjs cleanup`. Needs the migrator run with
  `ConnectionStrings:KeyRing`, `dotnet build src/Platform.Web`, `KEYCLOAK_ADMIN` and `KEYCLOAK_ADMIN_PASSWORD` in
  `.env` (`E2E_ENV_FILE` for a `.env` outside this checkout) and the `erp-postgres` container. Session cookies in
  `.state/keyring-session.json` until cleanup; results in `keyring-results.json`; exits non-zero when a step fails.
- **`observability.mjs`** — W-10 plan task 10, the observability pipeline end to end (spec
  `docs/superpowers/specs/2026-09-30-observability-design.md` sections 6.1, 6.7, 8 and 10). Steps: (1) `GET /dev/throw`
  on acme answers 500 with `X-Correlation-Id`; (2) within 60 seconds `traces-*` holds that trace and its server span
  carries `waslabid.tenant.id` of acme; (3) ES|QL on `logs-*` by `trace_id` finds one Error record with
  `exception.type`, `waslabid.component` `Web` and the tenant id, and only the three masked exception attributes; (4) the
  F-53 query of spec section 8 runs as a throwaway Elasticsearch user with the role `waslabid_errors_reader` (created as
  `elastic`, deleted at the end), counts the error and is refused (403) when indexing into `logs-*`; (5) after one run of
  `vendor.mjs` no log record of the last hour holds an email address (ES|QL `RLIKE` on `body.text`) or the run's CR
  number (`query_string` over all fields), each search with a positive control; (8) a throwaway acme staff admin keeps a
  page open: `metrics.waslabid.users.concurrent` for acme `staff` becomes 1, the Kibana dashboard `waslabid-usage`
  shows it to the Kibana staff user, `metrics.waslabid.users.active` follows the usage job, the console usage page (a
  throwaway platform admin with OTP) shows the same numbers in `ar-SA` and `en-US` with the Kibana link and no email or
  name, and closing the page brings the count back to 0; (9) Kibana's APM trace view and Discover open the trace by its
  id (risk O-4); (6) `docker stop erp-otel-collector`, then `erp-elasticsearch`: ten `GET /` on acme keep their usual
  time (p95 against a baseline taken just before, at most 100 ms added), one "[WaslaBid] Telemetry is down" email names
  the part within two minutes and one "has recovered" email follows the restart; (7) `/alive` answers 200 on acme and on
  the platform host, also with `erp-postgres` stopped, while `/health` answers 503. Steps run in the order 1, 2, 3, 4, 5,
  8, 9, 6, 7; pass numbers to run a subset (`node observability.mjs 1 2 3 4`; steps 2, 3, 4 and 9 add step 1). Steps 6
  and 7 always start the containers they stopped, even on failure, and the script checks all three are running at the
  end. Steps 8 and 9 start Kibana (`docker compose --profile kibana up -d kibana kibana-setup` in `infra/compose`) and
  stop it afterwards. Needs the default Compose stack with the collector and Elasticsearch, the web host and the worker
  (the worker with `Telemetry:ElasticsearchPassword` in its user secrets, the value of `ELASTIC_MONITOR_PASSWORD`),
  and in `.env` `ELASTIC_PASSWORD`, `KIBANA_STAFF_USER`, `KIBANA_STAFF_PASSWORD`, `KEYCLOAK_ADMIN` and
  `KEYCLOAK_ADMIN_PASSWORD`; credentials go into request headers only, never to the console. Elasticsearch and
  collector memory (`docker stats`, OOM kill and restart count) is sampled at the start, after steps 5, 8 and 9, after
  each restart and at the end. Screenshots in `shots-observability/`, the vendor run's output in
  `observability-vendor-run.log`, results with the recorded field names, the F-53 query and the p95 figures in
  `observability-results.json`; exits non-zero when a step fails. A full run takes about 30 minutes.

## What "run each script" looks like

None of these scripts assert and exit non-zero on failure; they print `PASS`/`FAIL` per step (`tenant.mjs`,
`platform.mjs`) or a JSON report (`golden.mjs`, `check.mjs`) for you to read. Treat any `FAIL` line, or a `noOverflow:
false`, as a defect to investigate before trusting the slice it covers.
