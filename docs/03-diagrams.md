# Diagrams: The Platform in Pictures

Date: 2026-09-21
Status: companion to `01-idea-competitors-features-ai.md` and `02-core-features-and-tech-stack.md`

Every diagram is Mermaid, so GitHub renders it in place. Each one has a two-line "how to read it" note. Feature IDs (F-xx) refer to document 02.

## 1. System context: who touches the platform

Read it as: the platform in the middle, people on the left and right, external systems below.

```mermaid
flowchart LR
    classDef person fill:#1E4E79,color:#fff,stroke:none
    classDef system fill:#2F5496,color:#fff,stroke:none
    classDef ext fill:#E7E6F5,color:#222,stroke:#9B96C9

    TA([Tenant admin]):::person
    CO([Contracts officer]):::person
    TE([Technical evaluator]):::person
    FA([Finance approver]):::person
    AU([Auditor]):::person
    VU([Vendor user]):::person
    PA([Platform admin, us]):::person

    P[["Tender-to-PO Platform<br/>white-label, multi-tenant"]]:::system

    KC[(Keycloak<br/>identity)]:::ext
    LLM[(Claude API<br/>offer review)]:::ext
    MAIL[(Email provider)]:::ext
    SMS[(Unifonic SMS)]:::ext
    ERP[(Customer ERP<br/>later integration)]:::ext
    IDP[(Customer Entra ID<br/>optional SSO)]:::ext

    TA -- branding, users, limits --> P
    CO -- publish, screen, award, PO --> P
    TE -- score offers --> P
    FA -- approve award --> P
    AU -- read audit log --> P
    VU -- register, submit offers --> P
    PA -- create tenants, monitor --> P

    P --> KC
    P --> LLM
    P --> MAIL
    P --> SMS
    P -. PO export .-> ERP
    KC -. federated login .-> IDP
```

## 2. Who does what, in order

Read it as: each column is a role, time runs top to bottom, and each arrow is a hand-off. The AI column only ever hands drafts back to a person.

```mermaid
sequenceDiagram
    participant RD as Requesting dept
    participant CO as Contracts officer
    participant V as Vendor
    participant AI as AI assist
    participant FA as Finance approver

    RD->>CO: Raise need + budget code
    CO->>CO: Draft tender, criteria, checklist (F-15 to F-19)
    CO->>V: Publish, invite from address book (F-14a, F-19, F-55)
    V->>V: Open link, register if new, land on tender (F-55, F-11, F-12)
    V->>CO: Clarification questions (F-21)
    CO-->>V: Answers (anonymised to all)
    V->>CO: Sealed technical + financial offer (F-22, F-23)
    Note over CO: Deadline closes submissions (F-24)
    CO->>AI: Run compliance pre-check (F-45)
    AI-->>CO: Draft pass / fail per checklist item
    CO->>CO: Confirm compliance screening (F-28)
    CO->>RD: Compliant offers for scoring
    RD->>AI: Request coverage matrix + draft scores (F-46, F-47)
    AI-->>RD: Drafts with evidence quotes
    RD->>CO: Locked technical scores (F-29, F-30)
    CO->>CO: Open financial envelopes, logged (F-23)
    CO->>AI: Financial sanity check (F-48)
    AI-->>CO: Arithmetic errors, outliers, missing lines
    CO->>CO: Comparison sheet + ranking (F-31, F-32)
    CO->>FA: Recommendation + justification
    alt Within delegation of authority
        FA-->>CO: Approved (F-33)
    else Needs change
        FA-->>CO: Returned with reason
    end
    CO->>V: Award / regret letters (F-35)
    CO->>CO: Issue PO, record in register (F-36)
```

## 3. Tender lifecycle: the state machine (F-27)

Read it as: boxes are states a tender can be in, arrows are the only allowed moves, and the label on each arrow is the role that can make the move.

```mermaid
stateDiagram-v2
    [*] --> Draft
    Draft --> Published: Contracts officer publishes
    Published --> Published: Amendment, new version (F-20)
    Published --> Clarification: Vendors ask, officer answers
    Clarification --> Published
    Published --> Closed: Submission deadline reached (server time)
    Closed --> ComplianceScreening: Contracts officer opens technical envelopes
    ComplianceScreening --> TechnicalEvaluation: Compliant offers passed
    TechnicalEvaluation --> TechnicalLocked: All evaluators submitted, officer locks (F-30)
    TechnicalLocked --> FinancialOpening: Officer opens financial envelopes (logged event)
    FinancialOpening --> FinancialEvaluation: Comparison sheet generated (F-31)
    FinancialEvaluation --> FinanceApproval: Officer submits recommendation (F-32)
    FinanceApproval --> FinancialEvaluation: Returned with reason
    FinanceApproval --> Awarded: Approved within DoA (F-33)
    Awarded --> [*]: PO issued (F-36)

    Draft --> Cancelled
    Published --> Cancelled
    Closed --> Cancelled
    ComplianceScreening --> Cancelled
    TechnicalEvaluation --> Cancelled
    FinancialEvaluation --> Cancelled
    FinanceApproval --> Cancelled
    Cancelled --> [*]: Vendors notified (F-34)
```

## 4. Sealed envelopes: how the price stays hidden (F-23)

Read it as: time runs top to bottom. The financial file is encrypted with a key that nobody can use until the opening event, and the opening itself is written to the audit log.

```mermaid
sequenceDiagram
    autonumber
    actor V as Vendor
    participant P as Vendor portal
    participant API as Platform API
    participant KMS as Cloud KMS
    participant S3 as Object storage
    participant DB as PostgreSQL
    actor CO as Contracts officer
    actor TE as Evaluators

    V->>P: Upload technical + financial files
    P->>API: Submit offer (before deadline)
    API->>KMS: Generate data key for this tender's financial envelope
    KMS-->>API: Data key (plaintext + wrapped)
    API->>S3: Store technical files (standard encryption)
    API->>S3: Store financial files encrypted with data key
    API->>DB: Store wrapped key, file hashes, timestamp
    API->>DB: Audit: OFFER_SUBMITTED
    API-->>V: Receipt PDF with hashes (F-25)

    Note over CO,TE: Deadline passes. Technical evaluation runs. Financial stays sealed.

    TE->>API: Submit scores
    CO->>API: Lock technical scores (F-30)
    API->>DB: Audit: TECHNICAL_LOCKED

    CO->>API: Open financial envelopes (names of officers present)
    API->>DB: Check state = TechnicalLocked
    API->>KMS: Unwrap data key
    KMS-->>API: Data key
    API->>S3: Decrypt financial files
    API->>DB: Audit: FINANCIAL_OPENED (who, when, witnesses)
    API-->>CO: Comparison sheet (F-31)
```

## 5. Login and tenant isolation: one request end to end

Read it as: the same user can never reach another tenant's data because the host, the token, and the database row policy must all agree. Caddy only terminates TLS; the tenant decision is made inside the app.

```mermaid
sequenceDiagram
    autonumber
    actor U as User (staff or vendor)
    participant B as Browser (tenders.customer.sa)
    participant GW as Caddy (TLS)
    participant KC as Keycloak (org per tenant)
    participant API as Platform API
    participant DB as PostgreSQL (RLS)

    U->>B: Open tenders.customer.sa
    B->>GW: GET /
    GW->>API: Forward with original Host header
    API->>API: Host -> tenant slug "customer"
    API->>B: Redirect to Keycloak login (org = customer)
    B->>KC: Login (password + TOTP, or customer's Entra ID)
    KC-->>B: Token with org = customer, roles
    B->>GW: GET /api/tenders (Bearer token)
    GW->>API: Forward (TLS terminated)
    API->>KC: Validate token signature and expiry
    API->>API: Assert token.org == host tenant, else 403
    API->>DB: SET app.tenant_id = customer
    API->>DB: SELECT ... FROM tenders
    DB->>DB: RLS policy: tenant_id = current_setting('app.tenant_id')
    DB-->>API: Only customer's rows
    API-->>B: JSON
```

## 6. Tenancy model: one platform, many brands, shared vendors

Read it as: tenants are isolated boxes, vendors are shared and can be approved by several tenants, and each layer enforces the boundary in its own way.

```mermaid
flowchart TB
    subgraph KCR["Keycloak realm: platform"]
        direction LR
        O1["Organization: Al-Faisal Co<br/>domain tenders.alfaisal.sa<br/>IdP: Entra ID"]
        O2["Organization: Najd Industries<br/>domain najd.platform.sa<br/>IdP: local"]
        O3["Organization: ... tenant N"]
        VG["Vendor users<br/>(same realm, role vendor)"]
        VG -- member, approved --> O1
        VG -- member, approved --> O2
    end

    subgraph DBX["PostgreSQL: one schema, RLS by tenant_id"]
        direction LR
        R1[(rows tenant_id = alfaisal)]
        R2[(rows tenant_id = najd)]
        RV[(vendor_company rows<br/>platform-wide)]
        RA[(vendor_tenant_approval<br/>vendor x tenant)]
    end

    subgraph ST["Object storage: one bucket per environment"]
        direction LR
        K1["alfaisal/tender-123/technical/"]
        K2["alfaisal/tender-123/financial/ (per-tender key)"]
        K3["najd/..."]
    end

    O1 --> R1 --> K1 & K2
    O2 --> R2 --> K3
    VG --> RV --> RA
```

## 7. Core data model, part 1: tenants, vendors, tenders

Read it as: boxes are tables, lines are relationships, and the crow's foot end is the "many" side. Every table except the vendor tables carries a tenant_id.

```mermaid
erDiagram
    TENANT ||--o{ TENANT_USER : has
    TENANT ||--|| BRANDING : has
    TENANT ||--o{ APPROVAL_LIMIT : defines
    TENANT ||--o{ TENDER : owns
    TENANT ||--o{ VENDOR_TENANT_APPROVAL : approves
    TENANT ||--o{ AUDIT_EVENT : logs

    VENDOR_COMPANY ||--o{ VENDOR_USER : employs
    VENDOR_COMPANY ||--o{ VENDOR_DOCUMENT : uploads
    VENDOR_COMPANY ||--o{ VENDOR_TENANT_APPROVAL : "is approved by"

    TENDER ||--o{ TENDER_VERSION : "amended as"
    TENDER ||--o{ BOQ_LINE : contains
    TENDER ||--o{ CRITERION : "scored by"
    TENDER ||--o{ CHECKLIST_ITEM : requires
    TENDER ||--o{ COMMITTEE_MEMBER : assigns
    TENDER ||--o{ CLARIFICATION : receives

    TENANT {
        uuid id PK
        string slug
        string name_ar
        string name_en
        string cr_number
        string plan
    }
    VENDOR_COMPANY {
        uuid id PK
        string cr_number
        string vat_number
        string name_ar
        string name_en
        decimal local_content_pct
    }
    TENDER {
        uuid id PK
        uuid tenant_id FK
        string reference
        string type "RFQ | RFP | TENDER"
        string state
        datetime submission_deadline
        string financial_method
        int min_technical_score
    }
```

## 7b. Core data model, part 2: offers, evaluation, award

Read it as: one offer per vendor per tender, two envelopes per offer, and every AI output stored as its own row next to the human decision.

```mermaid
erDiagram
    TENDER ||--o{ OFFER : receives
    TENDER ||--o| AWARD : "ends in"
    VENDOR_COMPANY ||--o{ OFFER : submits

    OFFER ||--|| TECHNICAL_ENVELOPE : has
    OFFER ||--|| FINANCIAL_ENVELOPE : has
    OFFER ||--o{ OFFER_LINE_PRICE : prices
    OFFER ||--o{ COMPLIANCE_RESULT : "screened by"
    OFFER ||--o{ SCORE : "scored in"
    OFFER ||--o{ AI_REVIEW : "reviewed by"

    AWARD ||--|| PURCHASE_ORDER : produces
    AWARD ||--o{ APPROVAL : "approved by"

    OFFER {
        uuid id PK
        uuid tenant_id FK
        uuid tender_id FK
        uuid vendor_company_id FK
        int version
        datetime submitted_at
        string status
    }
    FINANCIAL_ENVELOPE {
        uuid id PK
        uuid offer_id FK
        string object_key
        bytes wrapped_data_key
        datetime opened_at
        string opened_by
    }
    SCORE {
        uuid id PK
        uuid offer_id FK
        uuid criterion_id FK
        uuid evaluator_id FK
        decimal value
        boolean locked
    }
    AI_REVIEW {
        uuid id PK
        uuid offer_id FK
        string kind "compliance | coverage | scores | financial | integrity"
        string model
        string prompt_version
        string input_hash
        json output
        string human_decision
    }
    PURCHASE_ORDER {
        uuid id PK
        uuid award_id FK
        string po_number
        decimal total_ex_vat
        decimal vat
        string pdf_object_key
    }
```

## 8. Application modules and how they depend on each other

Read it as: arrows point from the module that calls to the module it depends on. Nothing points back up, which keeps the monolith splittable later.

```mermaid
flowchart TB
    classDef core fill:#2F5496,color:#fff,stroke:none
    classDef shared fill:#E7E6F5,color:#222,stroke:#9B96C9

    TEN[tenancy + branding]:::shared
    IDN[identity + roles]:::shared
    DOC[documents<br/>storage, scan, parse]:::shared
    NOT[notifications]:::shared
    AUD[audit log]:::shared

    VEN[vendors]:::core
    TDR[tenders + submissions]:::core
    EVA[evaluation]:::core
    AWD[awards + PO]:::core
    AI[ai review]:::core

    VEN --> TEN & IDN & DOC & NOT & AUD
    TDR --> VEN & TEN & IDN & DOC & NOT & AUD
    EVA --> TDR & IDN & AUD & NOT
    AWD --> EVA & TDR & DOC & NOT & AUD
    AI --> TDR & EVA & DOC & AUD
```

## 9. Deployment in a Saudi region

Read it as: everything inside the dashed box lives in one Saudi cloud region. Only email, SMS, and the model API cross the boundary, and none of them receive full offer files unless the provider is in-Kingdom.

```mermaid
flowchart TB
    subgraph INET["Internet"]
        U1[Tenant staff]
        U2[Vendors]
        DNS[Customer DNS<br/>CNAME tenders.customer.sa]
    end

    subgraph KSA["Saudi cloud region (data residency N-01)"]
        LB[Load balancer + TLS]
        subgraph K8S["Kubernetes cluster"]
            GW[Caddy<br/>on-demand TLS, 2 replicas]
            KC[Keycloak<br/>2 replicas]
            API[ASP.NET Core host<br/>Blazor + APIs, 3 replicas]
            WEB[Static web apps<br/>tenant-app, vendor-portal]
            WRK[Hangfire worker<br/>parse, OCR, PDF, AI, notify]
            AV[ClamAV]
            OBS[OpenTelemetry collector<br/>Prometheus, Grafana, Loki, Sentry]
        end
        PG[(Managed PostgreSQL<br/>primary + replica, daily backup N-07)]
        RD[(Redis)]
        S3[(Object storage<br/>encrypted, versioned)]
        KMS[(Key management)]
    end

    subgraph EXT["External providers"]
        MAIL[Email API]
        SMS[Unifonic]
        LLM[Claude API]
    end

    U1 & U2 --> DNS --> LB --> GW
    GW --> WEB & API & KC
    API --> PG & RD & S3 & KMS
    KC --> PG
    WRK --> PG & S3 & AV & KMS
    WRK --> MAIL & SMS & LLM
    API & WRK & GW & KC --> OBS
```

## 10. AI offer review: what happens to one offer

Read it as: left to right, one offer goes in, five kinds of draft come out, and nothing is final until the person in the diamond acts.

```mermaid
flowchart LR
    classDef ai fill:#FFF4CE,stroke:#C9A227,color:#222
    classDef human fill:#1E4E79,color:#fff,stroke:none

    IN[Offer files<br/>PDF, DOCX, XLSX<br/>AR or EN] --> PARSE[Parse + OCR<br/>PdfPig, Open XML, Tesseract]
    PARSE --> CHUNK[Page-anchored chunks<br/>+ embeddings in pgvector]
    RFP[RFP text, criteria,<br/>checklist, BoQ] --> CTX[Cached tender context]
    CHUNK & CTX --> P1[Compliance pre-check]:::ai
    CHUNK & CTX --> P2[Coverage matrix]:::ai
    CHUNK & CTX --> P3[Draft scores + quotes]:::ai
    LOCK{{Technical locked?}} --> P4[Financial sanity]:::ai
    ALL[All offers in tender] --> P5[Integrity flags]:::ai
    P1 & P2 & P3 & P4 & P5 --> UI[Side-by-side view<br/>draft vs offer page]
    UI --> H{Human confirms,<br/>edits, or overrides}:::human
    H --> STORE[(AI_REVIEW row:<br/>model, prompt version,<br/>input hash, decision)]
```

## 11. Roadmap: from idea to first paying customer

Read it as: bars are phases, and each phase ends with something a real person can use. Dates assume part-time effort by one or two people and will move.

```mermaid
gantt
    title Version 1 roadmap (indicative)
    dateFormat YYYY-MM-DD
    axisFormat %b %Y

    section Validate
    Interviews with 3 procurement managers      :v1, 2026-10-01, 21d
    Pricing check vs Reference App, pick first customer :v2, after v1, 14d

    section Design
    Design spec + ADRs (auth, tenancy, envelopes) :d1, after v2, 21d
    Clickable UI prototype (AR + EN)              :d2, after v2, 21d

    section Build core
    Tenancy, Keycloak orgs, branding (F-01..F-10) :b1, after d1, 30d
    Vendors + documents (F-11..F-14)              :b2, after b1, 21d
    Tender authoring + publishing (F-15..F-21)    :b3, after b1, 30d
    Submission + sealed envelopes (F-22..F-26)    :b4, after b3, 30d
    Evaluation chain (F-27..F-34)                 :b5, after b4, 30d
    Award, PO, letters (F-35..F-37)               :b6, after b5, 14d
    Notifications + audit + dashboards (F-38..F-44) :b7, after b4, 30d

    section Pilot
    Pilot with first customer, one live tender    :p1, after b6, 45d
    Security review + pen test (N-03)             :p2, after b6, 21d

    section AI
    AI compliance pre-check + coverage (F-45, F-46) :a1, after b5, 30d
    Draft scores, financial sanity, integrity (F-47..F-49) :a2, after p1, 30d
```

## 12. Feature map at a glance

Read it as: one branch per module, leaves are the feature IDs from document 02.

```mermaid
mindmap
  root((Tender-to-PO<br/>platform))
    Tenancy and branding
      F-01 provisioning
      F-02 white-label
      F-03 custom domain
      F-04 Arabic + RTL
      F-05 isolation
    Identity and roles
      F-06 staff accounts, SSO
      F-07 roles
      F-08 committees
      F-09 delegation of authority
      F-10 shared vendor identity
    Vendors
      F-11 registration
      F-12 documents with expiry
      F-13 local content
      F-14 tenant vendor list
      F-14a vendor address book
    Tenders
      F-15 types
      F-16 content + BoQ
      F-17 evaluation model
      F-18 templates
      F-19 visibility
      F-20 amendments
      F-21 clarifications
      F-55 invitation with registration
    Submission
      F-22 wizard
      F-23 sealed envelopes
      F-24 deadline
      F-25 receipt
      F-26 vendor dashboard
    Evaluation
      F-27 state machine
      F-28 compliance
      F-29 scoring
      F-30 locking
      F-31 comparison sheet
      F-32 ranking
      F-33 finance approval
      F-34 cancellation
    Award and PO
      F-35 letters
      F-36 PO PDF
      F-37 PO export
    Notifications
      F-38 channels
      F-39 events
      F-40 preferences
    Audit and documents
      F-41 event log
      F-42 audit export
      F-43 dashboards
      F-44 storage
    AI assist
      F-45 compliance pre-check
      F-46 coverage matrix
      F-47 draft scores
      F-48 financial sanity
      F-49 integrity flags
      F-50 AI audit record
    Platform operations
      F-51 health board
      F-52 connections registry
      F-53 logs and traces
      F-54 tenants and jobs
```
