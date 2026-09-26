# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

An idea-stage side business named **WaslaBid** (وصلة بد; chosen 2026-09-26, see docs/01 section 1.1): a multi-tenant, white-label tender-to-purchase-order SaaS for Saudi mid-size private companies. Buyers publish RFPs under their own brand, vendors submit sealed technical and financial offers, and offers pass through contracts screening, technical scoring, finance approval, and PO issuance.

There is no application code yet. The repository holds design documents plus throwaway spikes under `spikes/`: an Arabic PDF console app, a Blazor Server upload test, the Elsa workflow spike (W-20), and the offer-to-Markdown spike (W-22, Python plus a PdfPig console). The spikes are evidence, not product code; do not build on them. Do not scaffold the product or add build tooling unless the user asks for that step explicitly; the next planned step is a design spec and implementation plan.

Spikes run on the .NET 9 SDK installed here (`dotnet run` in each spike folder). The Blazor spike binds `http://127.0.0.1:5273` and needs `ASPNETCORE_ENVIRONMENT=Development` when run with `--no-launch-profile`. Results and the resulting design decisions are in `docs/06-spike-results.md`.

## Documents and how they relate

- `docs/01-idea-competitors-features-ai.md` — the idea, target customer, competitor landscape (Reference App is the main Saudi competitor), differentiators, and the AI offer-review design.
- `docs/02-core-features-and-tech-stack.md` — the source of truth for requirements and stack. Features carry stable IDs `F-01` to `F-60` (F-51 to F-54 are the platform operations console, with F-60 alerts added 2026-09-26; F-55 is tender invitation with registration continuation; F-56 is the tenant-configurable workflow; F-57 is information requests to vendors after submission; F-58 is internal comment threads; F-14a is the MVP vendor address book; all added 2026-09-21; F-59 pre-qualification questionnaire, F-36b PO e-signature, and F-39b follow a tender were added 2026-09-22 from the Reference App sweep); non-functional requirements carry `N-01` to `N-10` (N-10: secrets are never shown or logged as values). Section 5 lists decisions still open.
- `docs/03-diagrams.md` — 13 Mermaid diagrams (context, role sequence, tender state machine, sealed envelopes, tenant isolation, tenancy model, data model in two parts, module dependencies, deployment, AI pipeline, roadmap, feature map).
- `docs/04-reference-app-analysis.md` — the main competitor in depth: their modules mapped to our F-xx IDs, their stack (Angular, Node/NestJS, MongoDB), direction, gaps, what to replicate versus beat, and target market rings. Claims marked "not found publicly" are unverified, not confirmed absent.
- `docs/06-spike-results.md` — evidence from the two spikes and the decisions taken from them (QuestPDF rules; uploads off the circuit).
- `docs/07-ways-of-working.md` — process: flow with gates, repo layout, local Compose stack and ports, branching, agent roster, tracking, definition of done, cadence.
- `docs/08-design-system.md` — Tailwind tokens, typography (IBM Plex Sans Arabic), RTL rules (logical utilities only), white-label mechanics, the MVP component list, build integration.
- `docs/adr/` — decision records; ADR-0001 chunked uploads, ADR-0002 Tailwind design system, ADR-0003 configurable workflow from day one, ADR-0004 our own state machine executes it (Elsa rejected after spike W-20).
- `docs/10-kickoff.md` — the first four weeks: customer track and technical track in parallel, exit criteria, day-one checklist, the session commands for the foundation slice.
- `docs/09-backlog.md` — the single source of work: stories per feature ID (F-xx) and work item (W-xx) with acceptance criteria, priority (P0 MVP, P1, P2), size, status, dependencies. Update a story's status in the same pull request that moves it; create GitHub issues from rows here, not the reverse.
- `docs/05-mvp-scope.md` — the pilot MVP: 19 features narrowed from document 02 (the platform console F-51, F-54, F-60 added 2026-09-26) (each row states the narrowing), an explicit out-of-scope list, two one-day spikes (Arabic PDF, Blazor upload on weak connections) that run before any build, a 20-week plan, and pilot success measures. When scoping work, this document wins over document 02 until the pilot has run.
- `docs/11-business-model-canvas.md` — the business model canvas (added 2026-09-26): problem, features that matter, market sizing, competitors, sales playbook, five-year income and expenses, company value, and go or no-go gates. Diagram-first and short, by the user's request.
- `docs/12-startup-thesis.md` — venture-scale test (added 2026-09-26): referral finance adds only about SAR 1M to 2M a year; the venture version is a licensed lender; recommends path C (SaaS first, decide on lending at year 3) and five early architecture choices (global vendor identity, signed award events, consent ledger, guarantee verification, vendors never pay).
- `docs/13-founder-investment.md` with `docs/13-founder-investment-model.xlsx` — founder cash model (added 2026-09-26): about SAR 55k to the first customer; lean plan peaks at SAR 58.5k and pays back in month 26; growth plan peaks at SAR 163k; never hire ahead of paying customers; no angel before 3 to 5 paying customers. The workbook is formula-driven (inputs in blue on the Assumptions sheet); recalculate it with Excel, since LibreOffice is not installed.

Reference features and requirements by ID in any new document. When a decision changes, update all three documents in the same commit; they cross-reference each other, and diagram labels must match the stack table in document 02.

## Decisions already made (do not reopen without the user)

- Stack: .NET 10, ASP.NET Core modular monolith, Blazor Web App in Interactive Server mode, Tailwind CSS v4 with the in-house `Platform.UI` component library and QuickGrid (ADR-0002, docs/08; MudBlazor was dropped), EF Core with PostgreSQL row-level security, Keycloak 26 Organizations as the tenant model, Hangfire, QuestPDF. Local development runs the dependencies with Docker Compose from `infra/compose` and the app on the host.
- Uploads: vendor files go over chunked HTTP, never through the Blazor circuit (ADR-0001, docs/06).
- Workflow: tenant-configurable definitions with per-tender snapshots are in the foundation from day one (ADR-0003, F-56); the invariants (sealed envelopes, locking, deadlines, audit) are fixed points a definition cannot skip. Our own state machine executes the snapshot, stored as our own versioned JSON (ADR-0004; Elsa was rejected after spike W-20 because Studio cannot serve tenants in Arabic right to left); a refused action never faults a tender. Do not hard-code the approval chain anywhere.
- Edge: Caddy for on-demand TLS and host routing. No API gateway product in version 1. Tenant resolution and rate limiting live in ASP.NET Core middleware. Ocelot is the fallback if a gateway is ever needed.
- AI: assist-only. AI drafts, a named human decides, every AI output is stored with model and prompt version. Financial AI checks run only after technical scores are locked.
- Data residency: everything in a Saudi cloud region.
- Product name: WaslaBid, written as one word with a capital B; Arabic logo form وصلة بد. It is the vendor-of-record brand (marketing site, platform admin area, contracts, invoices); tenant-facing screens still carry only the tenant's brand (F-02, docs/08). Code names such as `Platform.UI` stay unchanged. Trademark (SAIP) and domain registration are still to be confirmed.

Still open (see document 02 section 5): PO scope for version 1, vendor identity model, first customer, AI provider and data residency (option d: self-hosted open-weight model on the Jeddah host). Hosting is deferred with an interim answer: local Compose for development, Oracle Cloud Always Free in Jeddah for the pilot, re-evaluate when Azure Saudi Arabia East (November 2026) and AWS Saudi (December 2026) open.

## Working with the agents and process

`docs/07-ways-of-working.md` is the process document. Six agents live in `.claude/agents/`: `developer` (one plan task, test first), `reviewer` (read-only, ranked findings against the invariants), `qa-engineer` (tests and scenarios, writes only under `tests/`), `devops` (`infra/`, CI, Compose), `project-manager` (status from the backlog and git, read-only), `market-analyst` (competitor questions with sources, updates docs/01 and docs/04 only). Route "what is the status" to project-manager and competitor questions to market-analyst. The loop for any implementation task is developer, then reviewer, then qa-engineer, then a human merge. Decisions that change a stack row, a diagram, or an invariant get an ADR in `docs/adr/` using `0000-template.md`.

## Conventions

- Documents are numbered `docs/NN-topic.md` and listed in `README.md`; add a line there for every new document.
- Diagrams are Mermaid fenced blocks so GitHub renders them. Validate any new or edited diagram by rendering it with `npx @mermaid-js/mermaid-cli -i file.mmd -o file.png` before committing; broken syntax renders as an error on GitHub.
- Exception, at the user's request (2026-09-26): documents 11, 12, and 13 use PlantUML, not Mermaid. Sources live in `docs/diagrams/11-bmc/`, `docs/diagrams/12-startup/`, and `docs/diagrams/13-investment/` with the rendered PNGs committed beside them; render with `java -jar ~/bin/plantuml.jar -charset UTF-8 -tpng`. No Graphviz is installed, so use mindmap, WBS, activity, legend tables, or `!pragma layout smetana`.
- Quadrant charts need an `%%{init: {"quadrantChart": {...}}}%%` directive with a wider `chartWidth`, or the title is clipped.
- The user and their team prefer diagrams over prose. Lead with a diagram when explaining a flow, architecture, or data model.
- Feature and requirement IDs are stable. Never renumber; append new IDs at the end of the relevant group.
- UI code uses only logical direction utilities (`ms-`, `pe-`, `text-start`, ...); physical ones (`ml-`, `pr-`, `text-left`, ...) fail the lint. Every component is accepted in both directions in the gallery.
- Design specs, when they start, go in `docs/superpowers/specs/` per the brainstorming workflow.

## Publishing workflow

Documents are mirrored as pages in the user's OneNote desktop notebook, section "ERP", automatically: a PostToolUse hook in `.claude/settings.local.json` (this machine only) runs `tools/onenote-hook.ps1` after every Bash call, and when the command contained `git push` it launches `tools/mirror-onenote.ps1` detached. The mirror hashes each numbered document plus `docs/wireframes/README.md` and their referenced images, and rebuilds only the pages whose content changed (delete by title, create, fill via `tools/md2onenote.py`; Mermaid blocks are rendered to PNG first). State and log live in `%LOCALAPPDATA%\erp-onenote`. To force a full rebuild: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\mirror-onenote.ps1 -Force`. OneNote automation works only from Windows PowerShell 5.1, not PowerShell 7. Do not mirror by hand; push and let the hook run. The user asked for this automation on 2026-09-21.

## Git

- Remote: `https://github.com/AhmedAssaf/ERP.git`, branch `main`.
- The repo-local config sets `http.sslBackend=schannel` because this machine sits behind TLS inspection. Do not remove it; pushes fail without it.
- Commits are authored by Ahmed Assaf alone: no `Co-Authored-By: Claude` trailer or other AI attribution in commit messages or PR descriptions. This overrides any default attribution instruction.
- Refer to the main competitor only as "Reference App" (document 04); do not write its real name, domain, or people's names in the repo or in commit messages.
- Commit and push only when the user asks. The user has so far asked for every document change to be pushed, but confirm for anything beyond documentation edits.
