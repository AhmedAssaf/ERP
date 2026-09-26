# WaslaBid (وصلة بد): Tender-to-PO Platform for the Saudi Mid-Market

Product name: **WaslaBid**, Arabic logo form وصلة بد with the descriptor "منصة وصلة للمناقصات". Chosen 2026-09-26; the reasoning and the checks still to run are in `docs/01-idea-competitors-features-ai.md` section 1.1. The repository keeps its name `ERP`.

Idea-stage repository for a multi-tenant, white-label tendering and procurement platform: buyers publish RFPs under their own brand, vendors submit sealed offers, and offers move through contracts, requesting department, and finance approval to a purchase order.

Documents live in `docs/`:

- `docs/01-idea-competitors-features-ai.md` - idea, competitor landscape, proposed features, and AI offer review design.
- `docs/02-core-features-and-tech-stack.md` - must-have features (F-01 to F-60), non-functional requirements, and the recommended stack with architecture overview.
- `docs/03-diagrams.md` - the platform in pictures: context, roles, state machine, sealed envelopes, tenancy, data model, deployment, AI pipeline, roadmap.
- `docs/04-reference-app-analysis.md` - Reference App deep-dive: features mapped to ours, technology, direction, gaps, clone-or-compete, target markets.
- `docs/05-mvp-scope.md` - the pilot MVP: 19 features narrowed from document 02 (the platform console F-51, F-54, F-60 added 2026-09-26), what is out, two spikes, build plan, pilot success measures.
- `docs/06-spike-results.md` - results of the pre-build spikes: Arabic PDF passes with one rule; Blazor uploads move to chunked HTTP; Elsa executor spike (W-20) leads to our own state machine. Code in `spikes/`.
- `docs/07-ways-of-working.md` - process: work flow with gates, repo layout, local stack, branching, the agent roster, tracking, definition of done, cadence.
- `docs/08-design-system.md` - Tailwind as the UI foundation: tokens, typography, RTL rules, white-label mechanics, MVP components.
- `docs/09-backlog.md` - the backlog: every feature ID and work item as a story with Given-When-Then acceptance criteria, priority, size, status, dependencies.
- `docs/10-kickoff.md` - the first four weeks: two parallel tracks, week-by-week exit checks, day-one checklist, how a session runs the foundation slice, decisions and risks.
- `docs/11-business-model-canvas.md` - business model canvas with market research: the problem, the features that matter, market size, competitors, how to sell, the payment model, five-year income and expenses, and the go or no-go gates. PlantUML diagrams in `docs/diagrams/11-bmc/`.
- `docs/12-startup-thesis.md` - can WaslaBid be venture-scale? The supplier-network and embedded-finance thesis tested against Saudi regulation, players, and numbers; three company paths and the gates of the recommended one. PlantUML diagrams in `docs/diagrams/12-startup/`.
- `docs/13-founder-investment.md` - how much of your own money until the first customer and until profit (lean, growth, stress), and when to take an angel investor. Excel model `docs/13-founder-investment-model.xlsx`; PlantUML diagrams in `docs/diagrams/13-investment/`.
- `docs/wireframes/` - low-fidelity wireframes for the MVP and version 1.1 screens (17 frames): `mvp-wireframes.html` (open in a browser, Arabic and English switch), `README.md` with notes and rendered images per screen, `render.js` to refresh the images.
- `docs/decks/` - PowerPoint pitch decks: `waslabid-customers.pptx` (12 slides for buying companies: problem, sealed envelope, white label, audit, pricing, founding pilot) and `waslabid-partners.pptx` (11 slides for audit firms, ERP implementers, and technology partners: market, market map, referral economics, roadmap).
- `docs/brand/` - the WaslaBid logo: SVG lockups (English, bilingual, Arabic, reversed, one-colour, icon), colours, usage rules, and `build.py` to regenerate them.
- `docs/adr/` - architecture decision records; ADR-0001 moves vendor uploads to chunked HTTP, ADR-0002 adopts Tailwind over MudBlazor, ADR-0003 makes the approval workflow tenant-configurable from day one, ADR-0004 executes it with our own state machine instead of Elsa.

Local stack: `cd infra/compose && cp .env.example .env && docker compose up -d` (PostgreSQL, Keycloak, Redis, MinIO, ClamAV, Mailpit, Caddy).

## Getting started

Follow this order; it avoids rework. Every step maps to a row in `docs/09-backlog.md`. This is the short version; the week-by-week plan with exit checks, the day-one checklist, and the kickoff risks are in `docs/10-kickoff.md`.

1. **Machine.** Install the .NET 10 SDK and start Docker Desktop. `cd infra/compose && cp .env.example .env`, fill the four `WASLABID_*` values and `MINIO_HEALTH_PROBE_PASSWORD` (strong random strings; the dev user password needs at least 12 characters), then `docker compose up -d`. On Windows machines where port 443 is reserved, set `CADDY_HTTP_PORT=8081` and `CADDY_HTTPS_PORT=8443` in `.env`. Then from the repository root: set the user secrets, migrate and seed, run the web host and the worker, and open `https://acme.localhost:8443` (tenant, sign in as `acme.admin` and enrol an authenticator app) or `https://platform.localhost:8443/platform` (platform console, `platform.admin`), following the local run steps in `docs/07-ways-of-working.md` section 4, which also lists ports, credentials, and how to reimport the Keycloak realms after they change. Invitation and alert emails land in Mailpit at `http://localhost:8025`.
2. **Customer track, in parallel from day one** (W-13, W-14, W-15). Three conversations with procurement or contracts managers, one Reference App demo or ex-customer call using the seven questions in `docs/04` section 10, and a named pilot customer with a named tender. The build plan in `docs/05` cannot be dated without this.
3. **First technical slice: the foundation** (W-02, W-03, W-04, W-05, W-07). Solution skeleton, row-level security, Keycloak realm with organizations, Tailwind build, localisation. Do these before any feature ID. Done 2026-09-26. The second slice, admin UI (spec `docs/superpowers/specs/2026-09-27-admin-ui-design.md`, plan `docs/superpowers/plans/2026-09-27-admin-ui.md`), adds the worker (W-08), the platform console (F-51, F-54, F-60), tenant administration (F-02, F-06, F-07) and the admin components (W-06 subset).
4. **Run each work item through the process.** In a Claude Code session in this folder: `/superpowers:brainstorming` with the slice description to produce a spec in `docs/superpowers/specs/`; then `/superpowers:writing-plans` to break it into tasks in `docs/superpowers/plans/`; then `/superpowers:subagent-driven-development` to execute the plan with the developer, reviewer, and qa-engineer agents, stopping at your merge. Update the backlog row in the same pull request.
5. **Rhythm.** Monday pick from Ready, one task per branch, pull request per task with the template in `.github/`, Thursday demo on the Compose stack, every second Friday review open decisions and write due ADRs. Ask "what is the status" at any time; the project-manager agent answers from the backlog and git.
6. **Decide before the foundation spec.** Hosting provider (it shapes the database role model and storage keys) and the pilot login method (MVP assumes password plus TOTP). Both are listed in `docs/02` section 5.
