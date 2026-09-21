# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

An idea-stage side business: a multi-tenant, white-label tender-to-purchase-order SaaS for Saudi mid-size private companies. Buyers publish RFPs under their own brand, vendors submit sealed technical and financial offers, and offers pass through contracts screening, technical scoring, finance approval, and PO issuance.

There is no application code yet. The repository holds design documents only. Do not scaffold projects, add build tooling, or write code unless the user asks for that step explicitly; the next planned step is a design spec and implementation plan, not code.

## Documents and how they relate

- `docs/01-idea-competitors-features-ai.md` — the idea, target customer, competitor landscape (Reference App is the main Saudi competitor), differentiators, and the AI offer-review design.
- `docs/02-core-features-and-tech-stack.md` — the source of truth for requirements and stack. Features carry stable IDs `F-01` to `F-50`; non-functional requirements carry `N-01` to `N-09`. Section 5 lists decisions still open.
- `docs/03-diagrams.md` — 13 Mermaid diagrams (context, role sequence, tender state machine, sealed envelopes, tenant isolation, tenancy model, data model in two parts, module dependencies, deployment, AI pipeline, roadmap, feature map).
- `docs/04-reference-app-analysis.md` — the main competitor in depth: their modules mapped to our F-xx IDs, their stack (Angular, Node/NestJS, MongoDB), direction, gaps, what to replicate versus beat, and target market rings. Claims marked "not found publicly" are unverified, not confirmed absent.
- `docs/05-mvp-scope.md` — the pilot MVP: 15 features narrowed from document 02 (each row states the narrowing), an explicit out-of-scope list, two one-day spikes (Arabic PDF, Blazor upload on weak connections) that run before any build, a 20-week plan, and pilot success measures. When scoping work, this document wins over document 02 until the pilot has run.

Reference features and requirements by ID in any new document. When a decision changes, update all three documents in the same commit; they cross-reference each other, and diagram labels must match the stack table in document 02.

## Decisions already made (do not reopen without the user)

- Stack: .NET 10, ASP.NET Core modular monolith, Blazor Web App in Interactive Server mode, MudBlazor (for right-to-left Arabic), EF Core with PostgreSQL row-level security, Keycloak 26 Organizations as the tenant model, Hangfire, QuestPDF, .NET Aspire for local development.
- Edge: Caddy for on-demand TLS and host routing. No API gateway product in version 1. Tenant resolution and rate limiting live in ASP.NET Core middleware. Ocelot is the fallback if a gateway is ever needed.
- AI: assist-only. AI drafts, a named human decides, every AI output is stored with model and prompt version. Financial AI checks run only after technical scores are locked.
- Data residency: everything in a Saudi cloud region.

Still open (see document 02 section 5): PO scope for version 1, vendor identity model, hosting provider, first customer.

## Conventions

- Documents are numbered `docs/NN-topic.md` and listed in `README.md`; add a line there for every new document.
- Diagrams are Mermaid fenced blocks so GitHub renders them. Validate any new or edited diagram by rendering it with `npx @mermaid-js/mermaid-cli -i file.mmd -o file.png` before committing; broken syntax renders as an error on GitHub.
- Quadrant charts need an `%%{init: {"quadrantChart": {...}}}%%` directive with a wider `chartWidth`, or the title is clipped.
- The user and their team prefer diagrams over prose. Lead with a diagram when explaining a flow, architecture, or data model.
- Feature and requirement IDs are stable. Never renumber; append new IDs at the end of the relevant group.
- Design specs, when they start, go in `docs/superpowers/specs/` per the brainstorming workflow.

## Publishing workflow

Documents are mirrored as pages in the user's OneNote desktop notebook, section "ERP". OneNote automation works only from Windows PowerShell 5.1 (`powershell.exe`), not PowerShell 7; the COM `GetHierarchy` call fails under `pwsh`. A page is recreated (delete, then create) when its document changes, because `UpdatePageContent` with a new outline appends rather than replaces. Ask before touching OneNote unless the user has asked for the mirror in the current task.

## Git

- Remote: `https://github.com/AhmedAssaf/ERP.git`, branch `main`.
- The repo-local config sets `http.sslBackend=schannel` because this machine sits behind TLS inspection. Do not remove it; pushes fail without it.
- Commit and push only when the user asks. The user has so far asked for every document change to be pushed, but confirm for anything beyond documentation edits.
