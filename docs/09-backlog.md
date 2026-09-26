# Backlog

Date: 2026-09-21
Status: the single source of work. GitHub issues are created from rows here when work starts, never the other way round.
Related: `02-core-features-and-tech-stack.md` (feature acceptance), `05-mvp-scope.md` (MVP narrowing), `07-ways-of-working.md` (how rows move), `08-design-system.md`

## How to read this file

- **Epics** are the modules from document 02 plus two non-feature epics (platform foundation, pilot and market).
- **Stories** carry the feature ID (F-xx) when they implement a feature, or a work ID (W-xx) when they do not. IDs never change.
- **Priority:** P0 ships in the MVP pilot (document 05), P1 in version 1.1, P2 in version 1. P0 acceptance is the narrowed MVP version; the full acceptance in document 02 applies when the P1 or P2 follow-up row is done.
- **Size:** S under a day, M one to three days, L a week. Anything larger is split before it becomes Ready.
- **Status:** Backlog, Ready (has a plan task), In progress, In review, In QA, Done. Update the row in the same pull request.
- **Acceptance criteria** are Given, When, Then. A story is Done when every criterion has a passing test or recorded evidence, and the definition of done in document 07 section 8 holds.

Dependencies use story IDs. A story is not Ready until its dependencies are Done.

## Summary

| Epic | Stories | P0 | P1 | P2 |
|---|---|---|---|---|
| E0 Platform foundation | W-01 to W-12, W-19, W-20 | 11 | 3 | 0 |
| E1 Tenancy and branding | F-01 to F-05, F-01b | 4 | 2 | 0 |
| E2 Identity, users, roles | F-06 to F-10, F-06b | 2 | 2 | 2 |
| E3 Vendor registration | F-11 to F-14, F-12b, F-14a | 3 | 1 | 2 |
| E4 Tender authoring | F-15 to F-21, F-55, F-19b, F-59 | 6 | 2 | 2 |
| E5 Offer submission | F-22 to F-26 | 3 | 2 | 0 |
| E6 Evaluation chain | F-27 to F-34, F-56, F-56b, F-57, F-58 | 7 | 5 | 0 |
| E7 Award and PO | F-35 to F-37, F-36b | 1 | 2 | 1 |
| E8 Notifications | F-38 to F-40, F-39b | 2 | 1 | 1 |
| E9 Audit and documents | F-41 to F-44 | 2 | 0 | 2 |
| E10 AI assist | F-45 to F-50 | 0 | 2 | 4 |
| E11 Pilot and market | W-13 to W-18 | 6 | 0 | 0 |
| E12 Platform operations console | F-51 to F-54 | 0 | 3 | 1 |

## E0 Platform foundation (W-01 to W-12)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| W-01 | Local Compose stack | P0 | S | Done 2026-09-21: all services healthy on Docker Desktop 29 (Keycloak in 50 s, ClamAV within its start period), bucket created, Caddy answering on the override port; second `up -d` clean | |
| W-02 | Solution skeleton: host, worker, modules, tests, UI project per document 02 section 4.5 | P0 | M | Done | W-01 |
| W-03 | PostgreSQL row-level security foundation: `TenantId` convention, EF interceptor setting `app.tenant_id`, app role, policy migration helper | P0 | M | Backlog | W-02 |
| W-04 | Keycloak realm export with Organizations, web client, worker client; OIDC wiring in the host | P0 | M | Backlog | W-01, W-02 |
| W-05 | Tailwind build: standalone CLI in MSBuild, `@theme` tokens, fonts self-hosted, physical-utility lint | P0 | S | Backlog | W-02 |
| W-06 | `Platform.UI` components for the MVP (document 08 section 6) with the gallery page in both directions | P0 | L | Backlog | W-05 |
| W-07 | Localisation: `IStringLocalizer` setup, `ar-SA` and `en-US` resources, culture switch, `dir` on `<html>` | P0 | S | Backlog | W-02 |
| W-08 | Hangfire with PostgreSQL storage, dashboard behind admin role, deadline job skeleton | P0 | S | Backlog | W-02, W-03 |
| W-09 | CI workflow: build, test with Testcontainers, format, Trivy, lint, Mermaid render check for docs | P0 | M | Backlog | W-02 |
| W-10 | Observability: OpenTelemetry, Serilog, health endpoints, Sentry | P1 | S | Backlog | W-02 |
| W-11 | Production Caddyfile with on-demand TLS and the tenant allow endpoint; Kubernetes manifests | P1 | M | Backlog | F-03 |
| W-12 | Backup and restore drill script for PostgreSQL and object storage | P1 | S | Backlog | W-11 |
| W-20 | Elsa 3 spike, one-week box: custom activities for the fixed points, per-tender snapshot execution, Arabic and white-label designer feasibility; ends in ADR-0004 choosing the executor | P0 | L | Done | W-01 |
| W-19 | Pilot environment on Oracle Cloud Always Free, Jeddah home region: one Arm VM running the Compose stack plus the app, HTTPS via Caddy, nightly volume backup to object storage | P0 | M | Backlog | W-01, W-09 |

Acceptance criteria:

- **W-01.** Given a clean clone with Docker Desktop running, when `cp .env.example .env && docker compose up -d` runs, then all eight services reach healthy within four minutes and `docker compose ps` shows no restarts. Given a second `up -d`, then no init script fails.
- **W-02.** Given the solution, when `dotnet build -warnaserror` and `dotnet test` run, then both succeed with at least one placeholder test per module project, and no module project references another module's internals.
- **W-03.** Given two tenants with rows in the same table, when a request authenticated for tenant A queries the table through EF Core, then only tenant A rows return; when the same query runs with raw SQL as the app role, then still only tenant A rows return; when the query runs with no tenant set, then zero rows return.
- **W-04.** Given the realm import on a fresh Keycloak, when the host starts, then login through Keycloak issues a token carrying the organization id, and the host maps it to a `TenantContext`.
- **W-05.** Given a `.razor` file containing `ml-4`, when CI lints, then the build fails naming the file and line. Given the tokens file, when the CSS builds, then `--color-primary` is a CSS variable in the output.
- **W-06.** Given the gallery page in Development, when opened with `ar-SA` and `en-US`, then every component in document 08 section 6 renders in both directions with no horizontal overflow at 360px width, and the qa-engineer screenshots are stored as the golden set.
- **W-07.** Given a user profile set to Arabic, when any page renders, then `<html lang="ar" dir="rtl">` is present and every visible string comes from the Arabic resource file with no fallback to English keys.
- **W-08.** Given a scheduled job, when the worker host runs, then the job executes once even with two worker instances, and the dashboard is reachable only by the tenant admin role.
- **W-09.** Given a pull request, when CI runs, then build, tests against real PostgreSQL and Keycloak, format check, container scan, utility lint, and Mermaid render check all report within ten minutes.
- **W-10.** Given a request, when it fails, then a trace with tenant id and correlation id appears in the collector and an event in Sentry.
- **W-11.** Given a tenant with a verified custom domain, when the first HTTPS request for that hostname arrives, then Caddy asks the allow endpoint, receives 200, obtains a certificate, and serves the tenant portal; when the hostname is unknown, then the allow endpoint returns 404 and no certificate is issued.
- **W-20.** Given the one-week box, when it ends, then ADR-0004 records the executor with evidence: whether a definition that skips locking or opens financial early is rejected by the executor, whether a running tender keeps its snapshot after the definition changes, whether the designer renders in Arabic right-to-left under a tenant's colour, and the measured time to implement one custom step; given the box overruns, then the in-house state machine is chosen.
- **W-19.** Given an Oracle Cloud tenancy with Jeddah as home region, when the provisioning script runs, then an Always Free Arm instance (or the documented paid fallback) hosts the Compose stack and the app, the pilot tenant's hostname serves over HTTPS, all data stays in the Jeddah region, and a nightly backup of the PostgreSQL and MinIO volumes lands in Jeddah object storage; given the instance is destroyed, when the restore script runs on a new one, then the pilot tenant is back within one hour.
- **W-12.** Given a nightly backup, when the restore script runs against an empty environment, then the platform starts and a chosen tender's files and rows are present with matching hashes.

## E1 Tenancy and branding (F-01 to F-05)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-01 | Tenant provisioning (MVP: by script) | P0 | S | Backlog | W-03, W-04 |
| F-01b | Tenant provisioning admin screen | P1 | S | Backlog | F-01 |
| F-02 | White-label branding: logo, primary colour, portal name | P0 | M | Backlog | W-05, W-06 |
| F-03 | Custom domain mapping with automatic TLS | P1 | M | Backlog | W-11 |
| F-04 | Arabic and English with right-to-left everywhere | P0 | (constraint) | Backlog | W-07 |
| F-05 | Tenant data isolation enforced in the database | P0 | (via W-03) | Backlog | W-03 |

- **F-01.** Given the provisioning script with name, CR number, plan, and default language, when it runs, then a tenant row, a Keycloak organization, and a default branding row exist, and the tenant admin receives an invitation email within one minute.
- **F-02.** Given a tenant admin uploads a logo and picks a primary colour, when any tenant or vendor page, email, or PDF renders for that tenant, then it shows that logo and colour and never the platform's; given a colour with contrast under 4.5:1 on white, when saved, then the system stores a darkened value and shows the admin the adjusted colour.
- **F-03.** Given a CNAME from `tenders.customer.sa` to the platform, when the admin enters the domain and verification passes, then the portal serves on that hostname over HTTPS within five minutes and emails link to it.
- **F-04.** Given any screen, email, or PDF, when rendered in Arabic, then layout is mirrored, all strings are Arabic, and numbers and references keep their internal order.
- **F-05.** Same criteria as W-03, re-run against every new table in every pull request.

## E2 Identity, users, roles (F-06 to F-10)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-06 | Staff accounts: invite by email, password plus TOTP | P0 | M | Backlog | W-04 |
| F-06b | Tenant SSO through Entra ID or any OIDC provider | P2 | M | Backlog | F-06 |
| F-07 | Roles: tenant admin, contracts officer, technical evaluator, finance approver (MVP), auditor (P1) | P0 | S | Backlog | F-06 |
| F-08 | Per-tender committee assignment onto snapshot steps | P1 | M | Backlog | F-07, F-56 |
| F-09 | Delegation of authority thresholds on approval steps | P1 | M | Backlog | F-56, F-33 |
| F-10 | One vendor account across all tenants with per-tenant approval | P2 | L | Backlog | F-11 |

- **F-06.** Given an invitation, when the invitee sets a password and enrols TOTP, then they can log in; when they enter a wrong TOTP three times, then the account locks for fifteen minutes and the event is audited.
- **F-07.** Given a user with only the technical evaluator role, when they open a tender's financial comparison, then they receive 403 and the attempt is audited.
- **F-08.** Given a committee of named evaluators, when a staff member outside it opens the tender's offers, then they see nothing; when an evaluator is removed, then their draft scores stay but they lose access.
- **F-09.** Given limits of one approver under 100k and two above 1M, when an award of 1.2M is submitted, then it requires two approvals and cannot be awarded after one.
- **F-10.** Given a vendor approved by tenant A and pending at tenant B, when the vendor logs in, then they see A's invitations and B's pending state, and tenant B never sees A's data.

## E3 Vendor registration (F-11 to F-14)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-11 | Vendor self-registration (MVP: names, CR, VAT, contact, email verified) | P0 | M | Backlog | W-04, W-06 |
| F-12 | Vendor documents with expiry (MVP: CR and VAT certificate; expired blocks submission) | P0 | M | Backlog | F-11, ADR-0001 |
| F-12b | Full document set, 30-day expiry reminders | P1 | S | Backlog | F-12, F-38 |
| F-13 | Local content and Saudization fields | P2 | S | Backlog | F-11 |
| F-14a | Tenant vendor address book: name, email, category, registered or not; pick invitees from it | P0 | S | Backlog | W-03 |
| F-14 | Tenant vendor list: pending, approved, blocked, invite, tag | P2 | M | Backlog | F-11 |

- **F-11.** Given the registration form, when a vendor submits with an unverified email, then the account is inactive until the link is clicked; when the CR number is not ten digits, then the form shows a specific error in the vendor's language.
- **F-12.** Given a CR certificate with an expiry date in the past, when the vendor opens the submission wizard, then the wizard blocks at step one and names the expired document; given an upload, then it goes through the chunked path and is virus-scanned before it is listed.
- **F-14a.** Given the address book, when the officer publishes a tender and picks three entries plus one new email, then all four receive invitations and the new email is saved to the book; given a vendor that later registers with a listed email, then the entry shows as registered without the officer doing anything.
- **F-13.** Given local content percentage and Saudi headcount fields, when an offer is compared, then the comparison sheet shows both columns.
- **F-14.** Given a blocked vendor, when they open an invitation from that tenant, then they see "not eligible" and cannot submit; given a category tag, when the officer filters, then only tagged vendors appear.

## E4 Tender authoring and publishing (F-15 to F-21)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-15 | Tender types (MVP: sealed Tender only; RFQ and RFP in P2) | P0 | S | Backlog | W-02 |
| F-16 | Tender content: title, reference, description, attachments, BoQ lines, deadlines | P0 | M | Backlog | F-15, F-07 |
| F-17 | Evaluation model: checklist, weighted criteria, pass mark (MVP: lowest compliant price) | P0 | M | Backlog | F-16 |
| F-18 | Templates | P2 | S | Backlog | F-16 |
| F-19 | Visibility (MVP: invited vendors by email only) | P0 | S | Backlog | F-16, F-11 |
| F-19b | Open tenders: public listing page under the tenant domain, self-registration onto the tender, listing switch-off | P1 | M | Backlog | F-19, F-55, F-02 |
| F-59 | Pre-qualification questionnaire attached to a tender; only vendors who pass can submit | P2 | M | Backlog | F-17, F-22, F-28 |
| F-20 | Amendments with versioning and notification | P1 | M | Backlog | F-16, F-38 |
| F-21 | Clarifications (MVP: public answers to all invited) | P0 | M | Backlog | F-19 |
| F-55 | Tender invitation by email with registration continuation and access to the tender | P0 | M | Backlog | F-11, F-14a, F-19, F-38 |

- **F-15.** Given the MVP, when an officer creates a tender, then the only type offered is the sealed two-envelope Tender and its stages are Draft, Published, Clarification, Closed, Compliance screening, Technical evaluation, Technical locked, Financial opening, Financial evaluation, Finance approval, Awarded, Cancelled; given the P2 follow-up, when RFQ is chosen, then the technical stages are skipped and the financial stages run directly after Closed.
- **F-16.** Given a draft tender, when the officer publishes without a submission deadline in the future, then publishing is refused with a specific message; when BoQ lines have a unit and quantity, then the vendor wizard prices exactly those lines.
- **F-17.** Given criteria weights of 40, 30, 20, when the officer publishes, then it is refused until the weights sum to 100; given a pass mark of 70, when an offer scores 69.5, then it is excluded from financial ranking.
- **F-19.** Given three invited vendors, when a fourth registered vendor opens the tender URL, then they get 404 and the attempt is audited.
- **F-19b.** Given a tender published as Open, when anyone opens the tenant's public listing page, then they see the title, reference, deadline, and a register-and-submit link, and nothing about other participants; when a new vendor follows it, then they go through registration and land on the tender exactly as with an invitation; given the officer switches the listing off, then the page no longer shows the tender, already-registered participants keep access, and the change is audited; given a tender published as Invited, then it never appears on the listing page and its URL returns 404 to non-invitees.
- **F-59.** Given a tender with a PQQ of pass-or-fail items, when a vendor opens the submission wizard, then the PQQ is the first step and a failed mandatory item stops the wizard with the reason shown; given a passed PQQ, when the officer runs compliance screening, then the PQQ answers appear beside the checklist; given the deadline has passed, when anyone edits the PQQ, then the edit is refused. Source: Reference App app release 25.4.9 PQQ layer (docs/04 section 12).
- **F-20.** Given a published tender, when the officer changes the scope attachment, then version 2 is created, all invited vendors are notified within one minute, and version 1 stays readable.
- **F-55.** Given an invitation sent to an unregistered email, when the recipient opens the link, then they see the tender title and the inviting company's brand and are taken into registration with the email pre-filled; when they complete registration, then they land on the tender with access and the officer's list shows them as registered; given they stop halfway, when they reopen the same link a day later, then registration resumes where it stopped. Given an already registered vendor, when they open the link, then after login they land on the tender directly. Given the link is opened after the submission deadline, then it shows an expired message in the vendor's language. Given the link is used with a different email than the one invited, then access is refused and the officer is notified. Given the officer's tender page, then each invitee shows sent, opened, registered, or submitted with timestamps.
- **F-21.** Given a vendor question before the clarification deadline, when the officer publishes an answer, then every invited vendor sees it without the asker's identity; when a question arrives after the deadline, then it is refused.

## E5 Offer submission (F-22 to F-26)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-22 | Submission wizard with chunked uploads (ADR-0001) | P0 | L | Backlog | F-12, F-16, W-06 |
| F-23 | Sealed envelopes: separate keys, financial unreadable until opening | P0 | L | Backlog | F-22 |
| F-24 | Deadline enforcement with server time, late refused, resubmission | P0 | M | Backlog | F-22, W-08 |
| F-25 | Submission receipt PDF with hashes | P1 | S | Backlog | F-22 |
| F-26 | Vendor dashboard | P1 | M | Backlog | F-22 |

- **F-22.** Given a 50 MB technical file on a Fast 3G profile with one forced WebSocket drop, when the vendor uploads, then the upload completes with zero data loss and the stored SHA-256 matches the file (the docs/06 scenario E test, automated).
- **F-23.** Given a submitted offer before the opening event, when any tenant user, the platform admin, or a direct database query as the app role reads the financial object, then the content is ciphertext; when the officer opens financial after locking, then the decrypt event records who and when.
- **F-24.** Given a deadline of 14:00 server time, when a submission completes at 14:00:01, then it is refused and the refusal is audited with the timestamp; when a vendor resubmits at 13:50, then the earlier submission is replaced and both are in the audit log.
- **F-25.** Given a completed submission, when the receipt is generated, then it lists each file with its SHA-256 and the server timestamp, in the vendor's language.
- **F-26.** Given a vendor with two invitations, one draft, and one expiring document, when they open the dashboard, then all four appear with the next action for each.

## E6 Evaluation chain (F-27 to F-34)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-56 | Configurable workflow: definition model, per-tender snapshot, executor, default template | P0 | L | Backlog | W-03, W-20 |
| F-56b | Tenant workflow editor screen: steps, departments, roles, approvers, thresholds, templates | P1 | L | Backlog | F-56, W-06 |
| F-27 | Tender state machine executing the workflow snapshot (MVP states only) | P0 | M | Backlog | F-16, F-56 |
| F-28 | Compliance screening pass or fail per checklist item | P0 | M | Backlog | F-27, F-23 |
| F-29 | Technical scoring, blind between evaluators until all submit | P0 | L | Backlog | F-28 |
| F-30 | Score locking before financial opening | P0 | S | Backlog | F-29 |
| F-31 | Financial comparison sheet with arithmetic check and Excel export | P0 | M | Backlog | F-30 |
| F-32 | Combined ranking and justified override | P1 | M | Backlog | F-31 |
| F-33 | Finance approval, approve or return with reason (MVP: one approver) | P0 | M | Backlog | F-31 |
| F-34 | Cancellation with reason and vendor notification | P1 | S | Backlog | F-27, F-38 |
| F-57 | Information requests to a vendor after submission: draft, officer sends, vendor replies with attachments by a deadline, reply attached to the offer, price immutability | P1 | M | Backlog | F-22, F-28, F-38, F-55 |
| F-58 | Internal comment threads on offers, steps, and scores with mentions, resolve, and convert to a vendor request | P1 | M | Backlog | F-08, F-29, F-41 |

- **F-56.** Given the default template, when a tender is published, then the tender stores a snapshot of the steps and the officer's committee assignment fills the roles; given the tenant definition is edited afterwards, then the running tender is unchanged; given a definition that places financial opening before score locking, when saved, then it is rejected with the fixed-point rule named; given a step with an all-of rule and two approvers, when one approves, then the step stays open and the audit row names who is pending.
- **F-56b.** Given the editor, when the admin adds a department step between screening and scoring and saves, then the next published tender has it and existing tenders do not; given a template is chosen, then its steps appear editable; every change is audited.
- **F-27.** Given each state, when a user whose role does not own the stage attempts a transition, then it is refused; when any transition succeeds, then an audit row with before and after state exists.
- **F-28.** Given an offer failing a mandatory checklist item, when screening is confirmed, then the offer is excluded, the vendor is notified with the item named, and the offer never appears in scoring.
- **F-29.** Given two evaluators, when only one has submitted scores, then the other cannot see them; when both have submitted, then the weighted average per offer is computed to two decimals.
- **F-30.** Given locked scores, when an evaluator tries to change one, then it is refused; given unlocked scores, when the officer tries to open financial, then it is refused.
- **F-31.** Given priced BoQ lines, when the sheet is generated, then totals equal quantity times unit price per line, VAT is 15 percent, an arithmetic mismatch in a vendor's own total is flagged, and the Excel export matches the screen.
- **F-57.** Given an evaluator drafts a question on an offer during scoring, when the officer sends it, then the vendor receives an email and sees a portal task with the deadline, and the request appears in the tender's request log with who asked; given the vendor replies with text and a file, then the reply is attached to the offer, visible to the committee, and audited; given a reply that includes new prices, when submitted, then the prices are not applied and the officer is warned; given scores are locked, when anyone tries to send a technical request, then it is refused; given the financial stage, when a question is sent, then it is sent only to vendors whose envelopes were opened.
- **F-58.** Given a comment thread on an offer with a mention of the Finance department, when a finance approver opens the tender, then the mention is listed for them; given a vendor session, when the offer is viewed, then no internal comment is visible in any response; given a comment converted to a request, then a draft F-57 request exists with the comment text and the officer must send it; given a resolved thread, then it stays readable and the audit row names who resolved it.
- **F-33.** Given a recommendation, when the approver returns it with a reason, then the tender goes back to financial evaluation and the reason is visible to the officer; when approved, then the state becomes Awarded.

## E7 Award and PO (F-35 to F-37)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-35 | Award and regret letters | P1 | S | Backlog | F-33, F-36 |
| F-36 | Branded PO PDF (QuestPDF, document 06 rules) | P0 | M | Backlog | F-33 |
| F-37 | PO structured export | P1 | S | Backlog | F-36 |
| F-36b | Electronic signature on the PO by named signatories, with an optional Saudi e-signature provider | P2 | M | Backlog | F-36, F-06, F-41 |

- **F-36.** Given an awarded tender, when the PO is generated, then it carries the tenant numbering pattern, the winning lines, VAT, payment terms, Arabic and English text with correct shaping, and matches the golden image within tolerance.
- **F-36b.** Given an approved award, when the named signatories confirm in the portal with MFA, then the PO PDF carries their names, timestamps, and a document hash in the footer and the event is audited; given a Saudi e-signature provider is configured for the tenant, when the PO is generated, then it is routed to that provider and the returned signed PDF replaces the draft. Source: Reference App pricing checklist "E-Signature" (docs/04 section 12).
- **F-35.** Given an award, when letters are sent, then the winner receives the award letter and every other compliant vendor receives a regret letter, each in the vendor's language.
- **F-37.** Given a PO, when exported, then the JSON contains every line with quantity, unit price, and VAT, and the CSV opens in Excel with Arabic intact.

## E8 Notifications (F-38 to F-40)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-38 | Email channel through the worker (Mailpit locally) | P0 | S | Backlog | W-08 |
| F-39 | MVP events: invitation, answer, deadline in 48 h, received, action required, award or regret | P0 | M | Backlog | F-38 |
| F-40 | SMS channel, in-app, digests and preferences | P1 | M | Backlog | F-39 |
| F-39b | Follow a tender to receive its stage notifications without being on the committee | P2 | S | Backlog | F-39, F-08 |

- **F-38.** Given an email job, when the provider fails, then the job retries with backoff up to five times and then raises an alert; no notification is ever sent twice for one event.
- **F-39b.** Given a user with the Contracts officer or Tenant admin role, when they follow a tender, then they receive stage-advanced and award events and see the tender in a Following list; when they are not on the committee, then they never see offers or scores. Source: Reference App app release 6.7.0 "Request Watchers" (docs/04 section 12).
- **F-39.** Given a deadline 48 hours away, when the scheduled job runs, then every invited vendor without a submission receives the reminder once, in their language, with the tenant's branding.

## E9 Audit and documents (F-41 to F-44)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-41 | Append-only event log with actor, tenant, action, entity, timestamp, IP | P0 | M | Backlog | W-03 |
| F-42 | Signed audit export bundle | P2 | M | Backlog | F-41 |
| F-43 | Dashboards: cycle time, savings, participation | P2 | M | Backlog | F-33 |
| F-44 | Document storage: encryption, virus scan, size and type limits | P0 | M | Backlog | W-01, ADR-0001 |

- **F-41.** Given any write or login, when it happens, then one audit row exists; given the app role, when it attempts UPDATE or DELETE on the audit table, then the database refuses.
- **F-44.** Given an upload containing the EICAR test signature, when it completes, then it is rejected before it is listed and the vendor sees a specific message; given a 101 MB file, then the chunk endpoint refuses with the limit stated.

## E10 AI assist (F-45 to F-50)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-45 | Compliance pre-check drafts | P1 | L | Backlog | F-28, F-50 |
| F-46 | Requirement coverage matrix | P2 | L | Backlog | F-45 |
| F-47 | Draft technical scores with quotes | P2 | L | Backlog | F-46 |
| F-48 | Financial sanity check after locking | P2 | M | Backlog | F-31 |
| F-49 | Integrity flags across offers | P2 | M | Backlog | F-45 |
| F-50 | AI audit record and per-tenant off switch | P1 | S | Backlog | F-41 |

- **F-45.** Given a closed tender with AI enabled, when screening opens, then each checklist item shows a draft pass, fail, or unclear with a page reference, and nothing is confirmed until the officer clicks; given AI disabled for the tenant, then no request leaves the platform.
- **F-48.** Given locked scores, when the financial check runs, then arithmetic errors and prices beyond the configured variance are flagged; given unlocked scores, then the check cannot be started.
- **F-50.** Given any AI output, when stored, then the row carries model, model version, prompt version, input hash, and the human decision, and the row is immutable after the decision.

## E11 Pilot and market (W-13 to W-18)

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| W-13 | Three interviews with procurement or contracts managers, written up | P0 | M | Backlog | |
| W-14 | Reference App demo or former-customer call answering the seven questions in document 04 section 10 | P0 | S | Backlog | |
| W-15 | First customer signed for the pilot with a named tender and date | P0 | L | Backlog | W-13 |
| W-16 | Pricing page draft: monthly per-tenant price, first tender free | P0 | S | Backlog | W-13 |
| W-17 | Pilot dry run script with fake vendors, producing the document 05 section 7 table | P0 | M | Backlog | F-01, F-02, F-06, F-07, F-11, F-12, F-14a, F-15, F-16, F-17, F-19, F-21, F-55, F-22, F-23, F-24, F-56, F-27, F-28, F-29, F-30, F-31, F-33, F-36, F-38, F-39, F-41, F-44 |
| W-18 | Pilot review and version 1.1 scope | P0 | S | Backlog | W-17 |

- **W-13.** Given three interviews, when written up, then each records current tools, last tender's cycle time, what Reference App or others quoted, and whether the vendor would see their brand; the document 01 "things to verify" list is updated.
- **W-14.** Given the Reference App demo or ex-customer call, when it is written up, then each of the seven questions in document 04 section 10 has an answer marked confirmed, denied, or still unknown with its source, and document 04 sections 3 and 6 are updated where an answer changed a verdict.
- **W-15.** Given a signed pilot agreement, when the tender is named, then the plan in document 05 section 6 gets calendar dates.
- **W-16.** Given the pricing draft, when reviewed, then it states a monthly per-tenant price with what is included, the first-tender-free offer with its conditions, the white-label domain add-on, and how the price was tested against at least two interview answers; no plan requires an implementation project.
- **W-17.** Given the dry run, when it completes, then every row of the document 05 section 7 table has a measured value and a pass or fail.
- **W-18.** Given the pilot review, when it is held, then every document 05 section 7 measure has its live-tender value beside the dry-run value, the contracts officer's and finance approver's willingness to pay is recorded verbatim, and the version 1.1 scope is a ranked list of backlog IDs with any new stories added with acceptance criteria.

## E12 Platform operations console (F-51 to F-54)

Added 2026-09-21 from the request for one admin page covering apps, logs, credentials, and connections. Platform admin only; secrets are shown as references, never values (N-10).

| ID | Story | Pri | Size | Status | Depends on |
|---|---|---|---|---|---|
| F-51 | Component health board | P1 | M | Backlog | W-10 |
| F-52 | Connections and credentials registry with test and rotate | P1 | M | Backlog | F-51, W-11 |
| F-53 | Logs and traces view with tenant and correlation filters | P1 | M | Backlog | W-10 |
| F-54 | Tenants and jobs overview with suspend, TLS re-issue, job re-run | P2 | M | Backlog | F-51, F-03, W-08 |

- **F-51.** Given the platform host, when a platform admin with MFA opens the board, then every component in the list shows status, version, latency, and last check within the last 60 seconds; given a tenant admin, when they request the same URL, then they receive 404; given PostgreSQL stopped, then its tile turns to failed within one check interval and the failure text names the component.
- **F-52.** Given the registry, when any page, API response, or log line is inspected, then no secret value is present, only the reference name; given the test action on the email connection, then the result and latency are shown and audited; given the rotate action, then the secret store receives a new version, the application picks it up without restart, and the last-rotated date updates.
- **F-53.** Given a correlation id from a failed request, when entered in the filter, then every log line and the trace for that request appear within five seconds; given a log line containing a vendor's email, then the email is redacted in the view.
- **F-54.** Given a tenant with a failing job, when the admin re-runs it, then the job executes once and the outcome is visible; given suspend, then the tenant's users receive a suspended page in their language and vendors can still read past receipts; every action produces an audit row naming the admin and the tenant.
