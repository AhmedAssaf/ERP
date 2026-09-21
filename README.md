# ERP: Tender-to-PO Platform for the Saudi Mid-Market

Idea-stage repository for a multi-tenant, white-label tendering and procurement platform: buyers publish RFPs under their own brand, vendors submit sealed offers, and offers move through contracts, requesting department, and finance approval to a purchase order.

Documents live in `docs/`:

- `docs/01-idea-competitors-features-ai.md` - idea, competitor landscape, proposed features, and AI offer review design.
- `docs/02-core-features-and-tech-stack.md` - must-have features (F-01 to F-50), non-functional requirements, and the recommended stack with architecture overview.
- `docs/03-diagrams.md` - the platform in pictures: context, roles, state machine, sealed envelopes, tenancy, data model, deployment, AI pipeline, roadmap.
- `docs/04-reference-app-analysis.md` - Reference App deep-dive: features mapped to ours, technology, direction, gaps, clone-or-compete, target markets.
- `docs/05-mvp-scope.md` - the pilot MVP: 15 features narrowed from document 02, what is out, two spikes, build plan, pilot success measures.
- `docs/06-spike-results.md` - results of the two pre-build spikes: Arabic PDF passes with one rule; Blazor uploads move to chunked HTTP. Code in `spikes/`.
- `docs/07-ways-of-working.md` - process: work flow with gates, repo layout, local stack, branching, the agent roster, tracking, definition of done, cadence.
- `docs/08-design-system.md` - Tailwind as the UI foundation: tokens, typography, RTL rules, white-label mechanics, MVP components.
- `docs/09-backlog.md` - the backlog: every feature ID and work item as a story with Given-When-Then acceptance criteria, priority, size, status, dependencies.
- `docs/adr/` - architecture decision records; ADR-0001 moves vendor uploads to chunked HTTP, ADR-0002 adopts Tailwind over MudBlazor.

Local stack: `cd infra/compose && cp .env.example .env && docker compose up -d` (PostgreSQL, Keycloak, Redis, MinIO, ClamAV, Mailpit, Caddy).
