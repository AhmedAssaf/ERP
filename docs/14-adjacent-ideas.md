# 14. Ten adjacent ideas with fewer competitors

Date: 2026-09-26
Status: ideas to test in the W-13 interviews, not decisions. Competition levels are our estimate from docs/01 and docs/04 and general knowledge; none has been verified with sources yet (ask the market-analyst agent for the top three).
Related: docs/01 section 3, docs/04 section 9, docs/11 section 2 (the four segments), docs/12 (path C), F-10, F-12, F-13, F-41, F-42, F-56, F-62, F-65, F-66

**Answer first.** Tender software is crowded at the top (Etimad, the Reference App, Monafasat, SAP). The work *around* a tender is not: before it (vendor compliance papers), beside it (bank guarantees, approvals), and after it (contract milestones, payment claims, supplier scores). Six of the ten ideas reuse more than half of what WaslaBid already has, so they can be sold as a wedge into a buyer that is not ready for full tendering, then grow into WaslaBid. Best three to test first: **2 vendor compliance vault, 5 subcontractor payment claims, 7 procurement audit checks**.

## 1. Where each idea sits

```mermaid
%%{init: {"quadrantChart": {"chartWidth": 720, "chartHeight": 520}}}%%
quadrantChart
    title Ten ideas: competition vs reuse of WaslaBid
    x-axis Little reuse --> Heavy reuse of WaslaBid
    y-axis Crowded --> Few competitors
    quadrant-1 Test first
    quadrant-2 New build, open field
    quadrant-3 Avoid
    quadrant-4 Add-on only
    "1 Guarantee tracker": [0.60, 0.60]
    "2 Vendor compliance vault": [0.85, 0.72]
    "3 Local content prep": [0.45, 0.45]
    "4 Contract milestones": [0.65, 0.70]
    "5 Subcontractor claims": [0.56, 0.78]
    "6 Approval engine": [0.82, 0.62]
    "7 Procurement audit checks": [0.62, 0.92]
    "8 Non-profit": [0.82, 0.35]
    "9 Surplus asset auctions": [0.30, 0.35]
    "10 Supplier scorecards": [0.72, 0.53]
```

## 2. The ten ideas at a glance

| # | Idea | Who pays | Competitors (estimate) | Reuse of WaslaBid | Score /10 |
|---|---|---|---|---|---|
| 1 | Bank guarantee tracker and verification | Buyers holding bid and performance guarantees | Bank portals only; Etimad covers government | F-66, F-41, notifications | 7 |
| 2 | Vendor compliance vault: CR, ZATCA, GOSI, Nitaqat, Chamber certificates with expiry, shared across buyers | Buyers (vendors free) | Buyer-owned portals (Aramco, SABIC); Reference App supplier module | F-10, F-12, F-64 | **8** |
| 3 | Local content score preparation for suppliers bidding on government work | Suppliers | Consultancies; Reference App through SAP Ariba | F-13 only | 5 |
| 4 | Post-award contract milestones: deliverables, variations, retention, expiry alerts | Buyers | Enterprise tools (Icertis, Agiloft) in English; Excel | F-36, F-41, F-56 | 7 |
| 5 | Subcontractor payment claims: progress claims, measurement against the BoQ, retention, approval chain | Main contractors | Procore and Aconex (enterprise, English); Excel and email | F-16 BoQ, F-56, F-65 | **8** |
| 6 | Approval and delegation-of-authority engine with signed decisions, sold alone | Finance and audit teams | Generic e-signature apps; ERP workflows | F-56, F-65, ADR-0004 | 6 |
| 7 | Procurement audit checks on the buyer's ERP export: split orders, single source, vendor bank account shared with staff, price outliers | Internal audit, CFO | Big 4 audits; enterprise analytics (English) | F-42, F-48, F-49 logic | **8** |
| 8 | Non-profit procurement for grant-funded organisations | Non-profits and grant makers | Tanafos (docs/01) | Almost all of WaslaBid | 5 |
| 9 | Surplus and scrap asset auctions for private companies | Sellers | Several auction platforms | Little | 3 |
| 10 | Supplier performance scorecards after the PO: delivery, quality, SLA penalties | Buyers | ERP modules (rarely used by mid-size) | F-10, F-43 | 6 |

## 3. Pros and cons

| # | Pros | Cons |
|---|---|---|
| 1 Guarantees | Real money at risk (expired or fake guarantees); easy to explain; F-66 is already planned; no licence needed if we verify and never issue (N-11) | Banks may not offer a verification channel to a startup; buyers may see it as a feature, not a product |
| 2 Compliance vault | Every buyer checks the same five documents, every vendor uploads them again and again; builds the vendor network that F-62 needs; vendors never pay (docs/12) | Needs 3 or more buyers before vendors see the value; government APIs for certificate checks may need approvals |
| 3 Local content | Mandatory for government tenders, so demand is steady | Rules change often; consultancy-heavy, hard to automate; sells to vendors, not our buyers |
| 4 Contract milestones | Every tender ends in a contract that nobody tracks; natural next step after PO (F-36) | Buyers expect it inside their ERP; value shows only months later |
| 5 Subcontractor claims | Construction is the beachhead (docs/04 section 9); claims are monthly, so usage is sticky; Arabic tools are rare; money disputes make it urgent | Needs deep construction knowledge (measurement, retention rules); large contractors may already use Procore |
| 6 Approval engine | We already build it (F-56); every company needs it | "Just a workflow" is easy for ERPs and e-signature apps to copy; hard to price high |
| 7 Audit checks | Sells to the governance segment (docs/11 section 2), which has budget; no system change for the buyer (one export); short sales story: "we found X in your data" | Needs data access, so trust and PDPL work up front; findings can embarrass staff, so sponsor must be the CFO or audit committee |
| 8 Non-profit | Donors and regulators demand clean procurement records; WaslaBid fits almost as is | Small budgets; Tanafos already there; different sales channel |
| 9 Asset auctions | Companies do have surplus to sell | Crowded; nothing reused; different business (payments, logistics) |
| 10 Scorecards | Cheap to add; feeds award decisions in the next tender | Low willingness to pay alone; better as a WaslaBid feature |

## 4. Use case flows

Read each one as: who starts it on the left, what the system does in the middle, what the paying customer gets on the right. Boxes marked with a feature ID reuse WaslaBid.

### Idea 1. Bank guarantee tracker and verification

```mermaid
flowchart LR
    V["Vendor uploads<br/>bid or performance guarantee"] --> R["Read issuer, amount,<br/>expiry, beneficiary"]
    R --> C{"Verified with<br/>the issuing bank? F-66"}
    C -->|yes| T["Tracked with<br/>expiry calendar"]
    C -->|no| X["Flagged to contracts officer,<br/>audited F-41"]
    T --> A["Alert 30 and 7 days<br/>before expiry"]
    A --> D{"Extend or<br/>claim?"}
    D -->|extend| V
    D -->|claim| L["Claim letter drafted<br/>for finance"]
```

### Idea 2. Vendor compliance vault

```mermaid
flowchart LR
    V["Vendor registers once<br/>by CR number F-10"] --> U["Uploads CR, ZATCA, GOSI,<br/>Nitaqat, Chamber certificates F-12"]
    U --> K["Expiry dates read<br/>and checked"]
    K --> S["Vendor shares the vault<br/>with a buyer, consent F-64"]
    S --> B["Buyer sees a green or red<br/>status per document"]
    K --> E["Reminder before expiry"]
    E --> U
    B --> W["Same vault reused<br/>for the next buyer"]
```

### Idea 3. Local content score preparation

```mermaid
flowchart LR
    S["Supplier bidding on<br/>a government tender"] --> I["Enters staff, spend,<br/>and asset data"]
    I --> C["Score calculated with<br/>the current rules"]
    C --> G["Gaps shown:<br/>what raises the score"]
    G --> P["Evidence pack prepared<br/>for certification"]
    P --> T["Score attached<br/>to the bid F-13"]
```

### Idea 4. Contract milestones after award

```mermaid
flowchart LR
    A["Award and PO issued<br/>F-65, F-36"] --> M["Contract milestones created:<br/>deliverables, dates, retention"]
    M --> D["Vendor marks<br/>a milestone delivered"]
    D --> R{"Buyer accepts?<br/>approval chain F-56"}
    R -->|yes| P["Payment released<br/>in the ERP"]
    R -->|no| F["Returned with reason"] --> D
    M --> V["Variation requested"] --> R
    P --> E["Alert before<br/>contract expiry"]
```

### Idea 5. Subcontractor payment claims

```mermaid
flowchart LR
    S["Subcontractor submits<br/>monthly claim"] --> Q["Quantities measured<br/>against the BoQ F-16"]
    Q --> E["Site engineer<br/>certifies"]
    E --> C["Commercial manager<br/>checks rates and retention"]
    C --> F{"Finance approves?<br/>F-56"}
    F -->|yes| P["Payment certificate<br/>signed F-65"]
    F -->|no| B["Back to subcontractor<br/>with comments"] --> S
    P --> L["Running totals: paid,<br/>retained, remaining"]
```

### Idea 6. Approval engine sold on its own

```mermaid
flowchart LR
    R["Staff raises a request:<br/>purchase, payment, contract"] --> A["Authority matrix picks<br/>approvers by amount and type"]
    A --> S["Each approver signs<br/>in order F-56"]
    S --> D{"All approved?"}
    D -->|yes| X["Signed decision record<br/>F-65, audit F-41"]
    D -->|no| N["Rejected with reason,<br/>requester notified"]
    X --> E["Pushed to the ERP<br/>or exported"]
```

### Idea 7. Procurement audit checks

```mermaid
flowchart LR
    X["Buyer uploads one<br/>ERP export: POs, vendors, payments"] --> R["Rules run:<br/>split orders, single source,<br/>shared bank accounts,<br/>price outliers F-48, F-49"]
    R --> F["Findings ranked<br/>by money at risk"]
    F --> A["Internal audit reviews<br/>each finding"]
    A --> D{"Real issue?"}
    D -->|yes| P["Audit bundle for the<br/>audit committee F-42"]
    D -->|no| C["Closed with reason,<br/>rule tuned"]
```

### Idea 8. Non-profit procurement

```mermaid
flowchart LR
    G["Grant received<br/>with procurement rules"] --> T["Non-profit publishes<br/>tender, WaslaBid flow"]
    T --> O["Vendors submit<br/>sealed offers F-23"]
    O --> E["Committee evaluates<br/>and awards F-65"]
    E --> R["Procurement file<br/>for the donor F-42"]
    R --> N["Shared with the grant maker<br/>and the regulator"]
```

### Idea 9. Surplus asset auctions

```mermaid
flowchart LR
    C["Company lists surplus<br/>equipment or scrap"] --> P["Photos, inspection date,<br/>reserve price"]
    P --> B["Registered buyers<br/>place bids"]
    B --> W{"Reserve<br/>met?"}
    W -->|yes| S["Winner pays,<br/>collection booked"]
    W -->|no| R["Relisted or<br/>sold by negotiation"]
```

### Idea 10. Supplier performance scorecards

```mermaid
flowchart LR
    P["PO delivered"] --> R["Receiver rates delivery,<br/>quality, documents"]
    R --> K["SLA breaches and<br/>penalties recorded"]
    K --> S["Vendor scorecard<br/>updated F-43"]
    S --> V["Vendor sees its score<br/>and can respond"]
    S --> N["Score shown when<br/>shortlisting the next tender F-17"]
```

## 5. What to do with this

```mermaid
flowchart LR
    I["W-13 interviews<br/>3 procurement managers"] --> Q["Add 3 questions:<br/>vendor papers, subcontractor claims,<br/>audit findings"]
    Q --> P{"Which pain is loudest<br/>and has a budget?"}
    P -->|"tendering"| W["Keep WaslaBid as planned"]
    P -->|"idea 2, 5 or 7"| S["Sell it as the wedge;<br/>WaslaBid follows in the same account"]
    P -->|"none"| G["Gate 1 no-go signal (docs/11)"]
```

1. Do not start a new product. Keep the foundation work and the interview plan in docs/10.
2. In each interview, after the tender questions, ask three short ones:
   - "How do you check vendor papers such as CR, ZATCA, and GOSI, and how often do they expire on you?" (idea 2)
   - "If you work with subcontractors, how do you handle their monthly claims and retention?" (idea 5)
   - "When internal audit reviews purchasing, what do they find, and how long does it take?" (idea 7)
3. If an idea wins in two of three interviews, write it up as a feature in docs/02 with a new ID and decide at gate 1 (W-31) whether it leads.
4. Ask the market-analyst agent to verify competitors for ideas 2, 5, and 7 before any decision; the levels above are estimates.
