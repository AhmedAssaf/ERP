# Ways of Working

Date: 2026-09-21
Status: the operating rules for this repository from now until the pilot. Short on purpose; every rule here is one we will actually follow.
Related: `05-mvp-scope.md` (what we build first), `06-spike-results.md` (decisions from evidence), `CLAUDE.md` (rules Claude Code follows)

## 1. Principles

1. **Documents before code, IDs before names.** Every piece of work traces to a feature ID (F-xx), a requirement (N-xx), or an ADR. If it has no ID, it is not planned.
2. **Small, finished, reviewed.** A task is a few hours, ends in a pull request, is reviewed by someone or something that did not write it, and is tested before it is called done.
3. **Evidence over opinion.** Spikes, tests, and command output settle arguments. Document 06 is the model.
4. **The pilot decides.** Until one real tender has run, `05-mvp-scope.md` outranks every wish list.
5. **Arabic is not a translation task.** Both languages ship in the same pull request or the pull request is not done.

## 2. How work flows

Read it as: left to right, one idea becomes merged code. The diamonds are gates; nothing skips a gate.

```mermaid
flowchart LR
    classDef doc fill:#E7E6F5,stroke:#9B96C9,color:#222
    classDef gate fill:#FFF4CE,stroke:#C9A227,color:#222
    classDef code fill:#1E4E79,color:#fff,stroke:none

    I[Idea or request]:::doc --> D[Numbered doc or ADR<br/>docs/NN-*.md, docs/adr]:::doc
    D --> S{Spec approved?<br/>brainstorming skill}:::gate
    S --> P[Implementation plan<br/>docs/superpowers/plans]:::doc
    P --> T[Backlog story ready<br/>docs/09, then a GitHub issue]:::doc
    T --> B[Branch<br/>feat/F-23-sealed-envelopes]:::code
    B --> DEV[developer agent or human<br/>test first, one task]:::code
    DEV --> R{reviewer agent<br/>invariants, tests, scope}:::gate
    R -- request changes --> DEV
    R -- approve --> Q{qa-engineer agent<br/>coverage, scenario, Arabic}:::gate
    Q -- defects --> DEV
    Q -- pass --> PR[Pull request<br/>template filled, CI green]:::code
    PR --> M{Human merge}:::gate
    M --> CD[devops agent<br/>deploy to staging, then production]:::code
```

Three gates that are never skipped: spec approval before planning, reviewer before qa-engineer, human merge before deploy.

## 3. Repository layout and where things go

| Path | What lives there | Owner |
|---|---|---|
| `docs/NN-*.md` | Numbered narrative documents. Add a README line for each. | Everyone |
| `docs/adr/` | Architecture decision records, one per decision, never edited after acceptance; superseded instead. | Architect (you) |
| `docs/superpowers/specs/` | Approved design specs per feature group, produced by the brainstorming workflow. | Whoever runs the spec session |
| `docs/superpowers/plans/` | Implementation plans broken into tasks with acceptance per task. | Whoever runs the planning session |
| `spikes/` | Throwaway experiments with a results write-up in `docs/`. Never imported by product code. | Anyone |
| `infra/compose/` | Local development stack. | devops |
| `infra/k8s/`, `infra/keycloak/`, `infra/caddy/` | Production and staging configuration. | devops |
| `src/` | The product, laid out as in `02-core-features-and-tech-stack.md` section 4.5. | developer |
| `tests/` | Unit, integration, UI tests. | qa-engineer and developer |
| `.claude/agents/` | The agent roster (section 6). | Everyone |
| `.github/` | Workflows, pull request template, issue templates. | devops |

## 4. Local environment

One command from a clean clone:

```
cd infra/compose
cp .env.example .env
docker compose up -d
```

| Service | Purpose | Port on localhost | Credentials from `.env` |
|---|---|---|---|
| PostgreSQL 16 with pgvector | Platform and Keycloak databases, RLS-ready roles | 5432 | POSTGRES_USER, POSTGRES_PASSWORD |
| Keycloak 26 | Identity, Organizations per tenant | 8080 (admin console), 9000 (health) | KEYCLOAK_ADMIN, KEYCLOAK_ADMIN_PASSWORD |
| Redis 7 | Circuit state, locks, rate limits | 6379 | none |
| MinIO | S3-compatible object storage, bucket `erp-dev` | 9002 (API), 9003 (console) | MINIO_ROOT_USER, MINIO_ROOT_PASSWORD |
| ClamAV | Virus scanning for uploads | 3310 | none |
| Mailpit | Catches all outgoing email, shows it in a web UI | 1025 (SMTP), 8025 (UI) | none |
| Caddy | Local TLS edge; `https://<tenant>.localhost` forwards to the app on the host | 80, 443 by default; set `CADDY_HTTP_PORT` and `CADDY_HTTPS_PORT` (for example 8081 and 8443) on Windows machines where 443 sits in a reserved range | none |

The .NET app runs on the host with `dotnet watch` on port 5273 so hot reload works. Caddy forwards `*.localhost` to it, which lets you test several tenants on their own hostnames without editing the hosts file.

Reset everything: `docker compose down -v` then `up -d` again. ClamAV takes up to three minutes on first start while it downloads signatures; its health check allows for that.

### Run the app locally

With the Compose stack up and the two `WASLABID_*` values filled in `infra/compose/.env`, run these once from the repository root (Git Bash). The commands read the values from `.env` into shell variables and never print them (N-10); user secrets live outside the repository.

```bash
env_value() { grep "^$1=" infra/compose/.env | cut -d= -f2- | tr -d '\r'; }
PGPW=$(env_value POSTGRES_PASSWORD)
WEB_SECRET=$(env_value WASLABID_WEB_CLIENT_SECRET)
# erp_app's password is the development value from infra/compose/postgres/init/01-databases.sql.
APP_DB="Host=localhost;Port=5432;Database=platform;Username=erp_app;Password=erp_app_dev_password"

dotnet user-secrets set "ConnectionStrings:Owner" "Host=localhost;Port=5432;Database=platform;Username=erp;Password=$PGPW" --project src/Platform.Migrator > /dev/null
dotnet user-secrets set "ConnectionStrings:Platform" "$APP_DB" --project src/Platform.Migrator > /dev/null
dotnet user-secrets set "ConnectionStrings:Platform" "$APP_DB" --project src/Platform.Web > /dev/null
dotnet user-secrets set "ConnectionStrings:Platform" "$APP_DB" --project src/Platform.Worker > /dev/null
dotnet user-secrets set "Oidc:ClientSecret" "$WEB_SECRET" --project src/Platform.Web > /dev/null
unset PGPW WEB_SECRET
```

Then migrate, seed the development tenants `acme` and `beta`, and start the app:

```bash
dotnet run --project src/Platform.Migrator -- --seed-dev
dotnet run --project src/Platform.Web
```

Background jobs run in a second process, the worker (W-08, Hangfire on PostgreSQL). Start it in another terminal; it opens no port and connects as `erp_app` through its own user secret `ConnectionStrings:Platform` (set above). On first start it creates its tables in schema `hangfire`, which migration `platform/0003_hangfire_schema.sql` prepares for it. The web host only enqueues; jobs wait in the database until a worker runs.

```bash
dotnet run --project src/Platform.Worker
```

The worker also runs the health-check recurring job every minute (F-51, plan task 3): PostgreSQL, MinIO, Keycloak,
ClamAV, SMTP, its own Hangfire heartbeat, and the web host's `/health`, each with a five-second timeout, results in
`ops.health_results`. The non-secret endpoints have Development defaults in `src/Platform.Worker/appsettings.Development.json`
(`Health:MinIo:ServiceUrl`, `Health:MinIo:BucketName`, `Keycloak:ManagementUrl`, `ClamAv:Host`/`Port`, `Smtp:Host`/`Port`,
`Platform:WebHealthUrl`); MinIO's access key and secret have no default and are never written to an appsettings file
(N-10) - set them as user secrets or environment variables before starting the worker:

```bash
env_value() { grep "^$1=" infra/compose/.env | cut -d= -f2- | tr -d '\r'; }
dotnet user-secrets set "Health:MinIo:AccessKey" "$(env_value MINIO_ROOT_USER)" --project src/Platform.Worker > /dev/null
dotnet user-secrets set "Health:MinIo:SecretKey" "$(env_value MINIO_ROOT_PASSWORD)" --project src/Platform.Worker > /dev/null
```

The same job (plan task 4, F-60 as narrowed) also sends one alert email when a check's incident opens, and one recovery
notice when it closes (`ops.incidents.notified_open`/`notified_close`); disk usage above the configured threshold on
the worker's own drive goes through the same incident pipeline as component "Disk", rather than being a board tile.
A Hangfire job that fails three attempts in a row sends one more alert (per job id), through a job-server-scoped
filter, not Hangfire's process-wide `GlobalJobFilters`. Settings, all in `Platform.Worker`'s configuration:
`Smtp:Host`/`Smtp:Port` (shared with the SMTP health check), `Smtp:From`, `Platform:AlertRecipients` (a list; empty
sends nothing rather than guessing a destination), `Platform:DiskAlertPercent` (default 80), and `Platform:BoardUrl`
(the link an alert email includes; a plain Development default until the platform console host lands in a later
task). Development defaults for the non-secret ones are in `src/Platform.Worker/appsettings.Development.json`; there
is still no default recipient, so no alert leaves a fresh checkout until one is configured. Mailpit (already in the
Compose stack) catches every alert in Development.

Open `https://acme.localhost:8443` (or the port in `CADDY_HTTPS_PORT`). Login only works through Caddy: it terminates TLS, which the OIDC correlation cookies need, and forwards the host with its port so the redirect URI is right. Plain `http://localhost:5273` cannot complete an OIDC login. Keycloak answers on `http://localhost:8080`; sign in as `acme.admin` or `beta.admin` with `WASLABID_DEV_USER_PASSWORD` from `.env`.

## 5. Branches, commits, pull requests

- **Trunk-based.** `main` is always deployable. Branches live for days, not weeks.
- **Branch names:** `feat/F-23-sealed-envelopes`, `fix/F-24-late-submission-clock`, `infra/caddy-on-demand-tls`, `docs/adr-0004-uploads`, `spike/quest-pdf-arabic`.
- **Commits:** conventional prefix, imperative mood, feature ID in the body or title: `feat(tenders): enforce submission deadline with server time (F-24)`.
- **One pull request per task.** Fill the template in `.github/PULL_REQUEST_TEMPLATE.md`. CI must be green. The reviewer and qa-engineer agent reports are pasted into the pull request as evidence.
- **Human merges.** Squash merge with the conventional title.

## 6. The agent roster

Six agents live in `.claude/agents/`: four build-time roles and two that answer questions. They are specialists, not a replacement for judgment; the orchestrating Claude Code session (or you) decides what to hand to whom.

| Agent | Does | Never does | Tools |
|---|---|---|---|
| `developer` | Implements one plan task or one feature ID, test first, inside module boundaries | Reviews itself, changes infrastructure, expands scope | All |
| `reviewer` | Reads the full changed code and its callers, reports ranked findings against the invariants (isolation, sealed envelopes, locking, audit, deadlines, Arabic parity), runs tests | Edits anything | Read-only plus Bash for tests |
| `qa-engineer` | Designs and writes tests, runs end-to-end scenarios on the Compose stack, produces pilot evidence | Writes production code, weakens assertions, adds retries | Read, Bash, Write under `tests/` |
| `devops` | Compose stack, CI, Caddy, Keycloak export, Kubernetes, backups, Saudi-region hosting | Application code | All |
| `project-manager` | Answers status questions from the backlog, git history, and the plan: done, in progress, blocked, next, open decisions, risks; checks backlog health | Marks anything done without evidence, estimates without a plan | Read-only plus git |
| `market-analyst` | Competitor and market questions: feature comparison mapped to F-xx, landscape refresh of docs/01 and docs/04 with sources, gap watch on our differentiators, proposed backlog rows | Adds backlog rows itself, states a competitor lacks a feature without a search | Read, web search and fetch, edits docs/01 and docs/04 only |

**The loop for every task:** developer implements, reviewer reviews, developer fixes, qa-engineer covers and runs, human merges. The superpowers subagent-driven-development skill runs exactly this loop when given a plan.

**Rules of use:**
1. One task per developer run. Give it the plan file and the task number, nothing else.
2. reviewer always runs after developer, on the code, never on the developer's summary.
3. qa-engineer runs before a feature ID is marked done, and before every pilot milestone.
4. devops changes to `infra/` get the same reviewer pass as code.
5. When an agent reports a conflict with docs/02, the docs win until a human changes them.
6. "What is the status" goes to project-manager; "what does competitor X have" goes to market-analyst. Both answer from evidence with sources and never change scope.

## 7. Tracking

- **The backlog is `09-backlog.md`.** Every feature ID and work item is a story there with acceptance criteria, priority, size, status, and dependencies. It is the source; issues and plans are derived from it.
- **GitHub Issues** hold tasks derived from backlog rows. Labels: `F-01` to `F-50` and `N-01` to `N-09` for traceability, `module:tenancy` through `module:audit` for ownership, `type:feature`, `type:bug`, `type:infra`, `type:docs`, `type:spike`.
- **Milestones:** `MVP pilot` (document 05 section 3), `Version 1.1`, `Version 1`.
- **Project board** columns: Backlog, Ready (has a plan task), In progress, In review, In QA, Done. An issue is Ready only when its plan task exists.
- **Decisions** go in `docs/adr/`. Anything that changed a stack row, a diagram, or an invariant gets an ADR. The first five to write, from decisions already taken: stack, edge and no gateway, tenancy model, uploads off the circuit, AI assist-only policy.

## 8. Definition of done

A task is done when all of these are true, and not before:

1. Tests exist for the behaviour and would fail without the change.
2. reviewer approved with no blockers or majors open.
3. qa-engineer ran the coverage and any scenario the task touches, with real output pasted in the pull request.
4. Arabic and English resources both updated; right-to-left checked on any changed screen.
5. Tenant isolation, sealed envelope, locking, audit, and deadline invariants untouched or explicitly re-tested.
6. Docs and diagrams updated in the same pull request when behaviour they describe changed.
7. New or changed UI components appear in the component gallery in both directions and both cultures (docs/08).
8. CI green, pull request template complete, merged to `main`.

## 9. Cadence for a two-person team

| When | What | Output |
|---|---|---|
| Monday, 30 minutes | Pick the week's tasks from Ready; confirm they trace to the MVP list | Board updated |
| Daily, async | One line each: done, next, blocked, in the project channel or the issue | None |
| Thursday, 45 minutes | Demo on the Compose stack of everything merged that week; write down what the pilot customer would say | Demo notes appended to the milestone |
| Every second Friday, 1 hour | Decision review: open decisions in docs/02 section 5, new ADRs, what the spikes and tests taught us | ADRs merged, docs updated |

## 10. What we deliberately do not do yet

No microservices, no message broker, no API gateway, no separate API layer for the UI, no native mobile apps, no Kubernetes before the pilot. The tender workflow is tenant-configurable data from day one (ADR-0003); our own state machine executes it (ADR-0004, decided by spike W-20 on 2026-09-26). Each of these has a trigger in `02-core-features-and-tech-stack.md` section 4.4 or `06-spike-results.md`; until the trigger fires, the answer is no.
