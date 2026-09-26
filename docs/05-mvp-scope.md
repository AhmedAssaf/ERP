# WaslaBid MVP Scope: One Pilot Tender, End to End

Date: 2026-09-21
Status: proposal. Defines the smallest product that can run one real tender for one real customer.
Related: `02-core-features-and-tech-stack.md` (feature IDs), `04-reference-app-analysis.md` (why these features)

## 1. The goal

Glossary: tender = مناقصة; the vendor portal and emails use the Arabic term.

One Saudi private company runs one real tender on the platform, from publishing to a signed PO, with at least five vendors submitting, and the contracts officer says they would run the next one on it too. Everything in this document exists to make that sentence true. Anything that does not is out.

## 2. How the MVP relates to version 1

Read it as: top row ships for the pilot, middle row follows once the pilot has run, bottom row completes version 1.

```mermaid
flowchart LR
    classDef mvp fill:#1E4E79,color:#fff,stroke:none
    classDef next fill:#2F5496,color:#fff,stroke:none
    classDef v1 fill:#E7E6F5,color:#222,stroke:#9B96C9

    subgraph R3["Version 1: full document 02"]
        direction TB
        C1[SSO, vendor identity<br/>across tenants F-10]:::v1
        C2[Templates<br/>F-18]:::v1
        C3[Local content F-13,<br/>vendor list F-14]:::v1
        C4[Audit export, dashboards<br/>F-42 F-43]:::v1
        C5[Remaining AI<br/>F-46 to F-49]:::v1
    end

    subgraph R2["Version 1.1: after pilot feedback"]
        direction TB
        B1[Custom domain F-03]:::next
        B2[Workflow editor, committees, DoA<br/>F-56b F-08 F-09]:::next
        B3[Amendments F-20,<br/>open tenders F-19b]:::next
        B4[Receipts + vendor dashboard<br/>F-25 F-26]:::next
        B5[Ranking, cancellation,<br/>vendor requests, comments<br/>F-32 F-34 F-57 F-58]:::next
        B6[Letters + PO export<br/>F-35 F-37]:::next
        B7[SMS + preferences<br/>F-39 F-40]:::next
        B8[AI compliance pre-check<br/>F-45 F-50]:::next
    end

    subgraph R1["MVP: pilot tender (21 features)"]
        direction TB
        A1[Tenant screen + logo<br/>F-01 F-01b F-02]:::mvp
        A2[Staff accounts + roles<br/>F-06 F-07]:::mvp
        A3[Vendor registration + docs<br/>F-11 F-12]:::mvp
        A4[Tender authoring<br/>F-15 F-16 F-17]:::mvp
        A5[Address book, invite,<br/>clarifications F-14a F-19 F-55 F-21]:::mvp
        A6[Sealed submission + deadline<br/>F-22 F-23 F-24]:::mvp
        A7[Workflow snapshot + screening,<br/>scoring, lock F-56 F-28 F-29 F-30]:::mvp
        A8[Comparison + finance approval<br/>F-31 F-33]:::mvp
        A9[PO PDF<br/>F-36]:::mvp
        A10[Email + audit log<br/>F-38 F-41]:::mvp
        A11[Platform console: health, connections,<br/>errors, tenants, alerts<br/>F-51 F-52 F-53 F-54 F-60]:::mvp
    end
```

## 3. MVP feature list

Every row is a feature ID from document 02 with the MVP-sized version of its acceptance. Where the MVP is narrower than document 02, the narrowing is stated.

| # | ID | MVP version | Narrowed from document 02 |
|---|---|---|---|
| 1 | F-01, F-01b | Platform admin creates a tenant from a screen on the platform host: name, CR number, plan, default language, admin email (changed 2026-09-26, ADR-0005; the script stays for development seeding) | No self-serve sign-up |
| 2 | F-02 | Logo, one primary colour, portal name. Applied to both portals, emails, and the PO | No favicon, no accent colour |
| 3 | F-06 | Tenant admin invites staff by email. Password login with TOTP through Keycloak | No SSO |
| 4 | F-07 | Roles: Tenant admin, Contracts officer, Technical evaluator, Finance approver | No Auditor role. Auditor reads the log as Tenant admin |
| 5 | F-11 | Vendor self-registration: names in Arabic and English, CR, VAT, contact, email verified | No IBAN, no activity categories, no phone verification |
| 6 | F-12 | CR and VAT certificate with expiry dates. Expired document blocks submission | Two document types instead of seven. No 30-day reminder |
| 7 | F-15, F-16 | One tender type: sealed two-envelope Tender. Title, reference, description, scope attachment, terms attachment, BoQ lines, submission deadline, clarification deadline | No RFQ or RFP type, no bid bond field, no validity period |
| 8 | F-17 | Compliance checklist items, technical criteria with weights summing to 100, minimum pass mark, financial method fixed to lowest compliant price | No weighted technical-financial split |
| 9 | F-14a, F-19, F-55, F-21 | Officer picks invitees from the tenant's vendor address book or types new emails. Each invitee gets a branded single-use link: registered vendors land on the tender after login, unregistered ones register with the email pre-filled and land on the tender, resumable until the deadline. Officer sees sent, opened, registered, submitted per invitee. Vendors ask questions; officer answers publicly to all invited vendors | No open tenders, no private answers, no vendor categories beyond a text tag |
| 10 | F-22, F-23, F-24 | Wizard: documents check, technical upload, BoQ prices, financial upload, submit. Technical and financial stored with separate keys; financial unreadable until opening. Server-time deadline, late refused, resubmission allowed before deadline | No draft autosave beyond the browser session. Resubmission replaces rather than versions |
| 11 | F-28, F-29, F-30 | Officer marks checklist pass or fail per offer. Evaluators score each criterion, hidden from each other until all submit. Officer locks scores | No "waived" state |
| 12 | F-31, F-33 | Auto-built comparison sheet: vendor, BoQ line prices, totals, VAT, arithmetic check. Export to Excel. One finance approver approves or returns with a reason | No internal estimate variance, no local content column, no approval limits |
| 13 | F-36 | Branded PO PDF with tenant numbering, winning offer lines, VAT, payment terms text. Stored against the tender | No PO register screen, no structured export |
| 14 | F-38, F-39 | Email only: invitation, question answered, deadline in 48 hours, submission received, action required, award or regret | No SMS, no in-app, no digests |
| 16 | F-56 | Workflow definition model, per-tender snapshot, and executor with one default template (contracts screening, technical evaluators, finance approver). No editor screen; the template is seeded by script | Editor screen is F-56b in version 1.1; executor is our own state machine (ADR-0004, spike W-20) |
| 15 | F-41 | Append-only event table: actor, tenant, action, entity, timestamp, IP. Visible to Tenant admin as a filterable list | No signed export |
| 17 | F-51 | Platform admin page on the platform host, MFA required: status, latency, last check, and last failure for web host, worker, PostgreSQL, object storage, Keycloak, ClamAV, and the email provider | No version column, no SMS or AI provider tiles (neither is in the MVP), no edge tile |
| 18 | F-54 | Tenant list: status, user count, active tenders, storage used, failing jobs. One action: re-run a failed job, confirmed and audited | No suspend or resume, no TLS re-issue (no custom domains in the MVP) |
| 19 | F-60 | Email to the platform admin, once per incident plus a recovery notice, when a health check fails, a job fails three times, a deadline-closure job has not run five minutes after a deadline, or disk passes 80 percent. Incidents of the last 30 days listed on the F-51 page. Thresholds in configuration | No SMS, no TLS alerts, no threshold screen |
| 20 | F-52 | Connections registry on the platform host: endpoint, account, secret reference, owner, last rotated, and a test action, audited. Added 2026-09-26 (ADR-0005) | No rotate action; rotation is done in the secret store by hand |
| 21 | F-53 | 24-hour error summary per component on the F-51 page, read from Loki, with a link to Grafana for search and traces. Added 2026-09-26 (ADR-0005) | No log search or trace view in the console; Grafana over Loki and Tempo serves them until after three to five paying customers |

Also in the MVP because the pilot cannot run without them, though they carry no feature ID: Arabic and English UI with right-to-left (F-04 is treated as a constraint, not a feature), and the tender state machine (F-27) limited to the states the twenty-one features need.

## 4. Explicitly out of the MVP

Custom domains, SSO, the workflow editor screen (F-56b; the model ships, the pilot uses the seeded default), per-tender committees, delegation of authority limits, information requests to vendors after submission and internal comment threads (F-57, F-58; during the pilot the officer emails the vendor and the reply is filed by hand), amendments, open tenders with a public listing page (F-19b; the pilot is invited-only), receipts with hashes, vendor dashboard, ranking other than lowest price, cancellation, award and regret letters, PO export, SMS, notification preferences, local content fields, the full tenant vendor list with approval states (the address book F-14a is in), templates, public listing, audit export, dashboards, the console's log search and trace view (rest of F-53; the pilot uses Grafana over Loki and Tempo, and Sentry), credential rotation from the console, consented support access (F-61), every AI feature, mobile apps, ERP integration, vendor identity across tenants.

If the pilot customer asks for one of these, the answer is "version 1.1, after your tender closes", unless the tender cannot legally proceed without it.

## 5. The spikes to run before anything else

The first two are one-day experiments whose failure would change the stack; both ran on 2026-09-21 (document 06). The third is a one-week box added by ADR-0003.

| Spike | Question | Pass condition | If it fails |
|---|---|---|---|
| Arabic PDF | Can QuestPDF render a PO with mixed Arabic and English, correct shaping, right-to-left tables, and a Saudi font? | A one-page PO with Arabic vendor name, Arabic terms paragraph, and a numeric BoQ table prints correctly | Switch to Playwright for .NET with headless Chromium rendering HTML |
| Elsa workflow executor (W-20) | Can Elsa 3 execute a per-tender workflow snapshot while our custom activities make the fixed points (locking before financial opening, sealed envelopes, deadlines) impossible to skip, and can its designer be delivered in Arabic under the tenant's brand? | A definition that reorders a fixed point is rejected; a running tender ignores later definition edits; one custom step implemented in under a day; designer renders right-to-left | Our own state machine executes the same definition model; ADR-0004 records it. **Ran 2026-09-26:** executor checks pass, designer fails right to left, so the fallback applies (docs/06 section 6) |
| Blazor on a vendor's connection | Does a Blazor Server upload wizard survive a 50 MB file on a throttled 3G profile with a 2-second latency spike? | Upload completes, circuit reconnects after the spike, draft state survives | Move the vendor portal pages to static server rendering with plain form posts, keep Blazor Server for the tenant app |

## 6. Build plan

Read it as: one developer, roughly half time. Two developers compress the middle bars by about half. Dates are relative to the start, not calendar dates.

```mermaid
gantt
    title MVP build plan, one developer at half time
    dateFormat YYYY-MM-DD
    axisFormat Week %W

    section Spikes
    Arabic PDF spike                 :s1, 2026-10-05, 2d
    Blazor vendor upload spike       :s2, 2026-10-07, 2d
    Elsa workflow executor spike (W-20) :s3, 2026-10-09, 5d

    section Foundation
    Compose stack, Keycloak, Caddy, CI :f1, after s3, 7d
    Tenant, branding, RLS, audit, workflow model (1, 2, 15, 16) :f2, after f1, 10d
    Staff accounts, roles (3, 4)     :f3, after f2, 5d

    section Vendors and tenders
    Vendor registration, documents (5, 6) :v1, after f3, 7d
    Tender authoring, criteria (7, 8) :v2, after f3, 10d
    Invitations, clarifications, email (9, 14) :v3, after v2, 7d

    section Submission and evaluation
    Sealed submission, deadline (10) :e1, after v3, 12d
    Screening, scoring, lock (11)    :e2, after e1, 10d
    Comparison sheet, finance approval (12) :e3, after e2, 7d
    PO PDF (13)                      :e4, after e3, 5d
    Platform console and alerts (17, 18, 19) :e5, after e4, 5d

    section Pilot
    Arabic and English pass, RTL fixes :p1, after e5, 7d
    Internal dry run with fake vendors :p2, after p1, 5d
    Pilot customer onboarding        :p3, after p2, 5d
    Live tender runs                 :p4, after p3, 30d
    Pilot review, version 1.1 scope  :p5, after p4, 5d
```

About 16 weeks from first spike to the live tender at the durations shown (one week for the workflow spike, one for the workflow model, one for the platform console added 2026-09-26), then a 30-day tender window and a review, so about 19 weeks to the pilot verdict. Plan for 17 to 19 weeks to the live tender if a spike fails or the customer is late. Two developers bring the build portion to about 8 weeks.

## 7. What the pilot must prove

| Question | Measure | Target |
|---|---|---|
| Will vendors use it? | Invited vendors who submit | At least 5 of 8 |
| Does the sealed process hold? | Attempts to read financial before opening, from logs | Zero successful, every attempt logged |
| Is it faster than email and Excel? | Days from close to finance approval | Under 5 working days |
| Is Arabic good enough? | Vendor and evaluator complaints about language or layout | Zero blocking, under 5 cosmetic |
| Would they pay? | Contracts officer and finance approver asked at review | Both say yes to a monthly price you name |
| What is missing? | Features requested during the tender | Ranked list feeds version 1.1 |

## 8. Decisions this plan assumes

- PO scope: branded PDF only (closes open decision 1 in document 02 for the MVP).
- Vendor identity: one account per vendor company, registered on the pilot tenant. Cross-tenant identity waits for version 1 (open decision 2 stays open).
- Hosting: any Saudi-region VM or small Kubernetes cluster for the pilot; the provider decision (open decision 3) can wait until version 1.
- First customer: still to be named. The plan cannot start section "Pilot" without one, so finding them runs in parallel with the spikes and foundation.
