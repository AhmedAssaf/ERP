# ADR-0004: Execute the per-tender workflow snapshot with our own state machine

Date: 2026-09-26
Status: Accepted
Deciders: Ahmed Assaf
Related: F-27, F-30, F-41, F-56, F-56b, W-20; ADR-0002, ADR-0003; `docs/06` section 6

## Context

ADR-0003 made the approval chain tenant-configurable data and left the executor to spike W-20, with a rule: Elsa 3 executes the snapshot only if its custom activities can guarantee the fixed points and its designer can be delivered in Arabic, right to left, under the tenant's brand; otherwise our own state machine does. The spike (docs/06 section 6) found that Elsa runs the chain correctly from a snapshot stored on our own row, but the fixed points were guaranteed by our validator and our `Tender` aggregate, not by Elsa. Elsa Studio does not support right to left, is only partly translated to Arabic, and brings MudBlazor, which ADR-0002 dropped.

## Decision

1. **Our own state machine executes the snapshot.** The engine is a module in the monolith that walks the snapshot's ordered steps (department, actors, any-of or all-of, amount thresholds, system stage) between the fixed points. No workflow engine package.
2. **The snapshot is our definition format**, versioned JSON on the tender row, copied at publishing (ADR-0003 point 2). No third-party serializer sits between a running tender and its chain.
3. **The fixed points stay in the domain.** Publishing validates that every path passes locking before financial opening; the `Tender` aggregate refuses out-of-order calls regardless of the definition.
4. **A refused action never faults a tender.** A decision from someone who is not on the step is refused and audited (F-41) while the step keeps waiting; only a broken invariant stops the tender.
5. **The editor (F-56b, version 1.1) is an ordered step list in `Platform.UI`**, in Arabic and English, right to left first, not a flowchart canvas.

## Consequences

- One dependency fewer, and no Elsa upgrade has to read snapshots from tenders that run for months.
- We write about what the spike needed anyway: step execution, any-of and all-of, durable waiting (a row per open step), and restart safety. Deadlines and reminders use Hangfire (F-24).
- Branching flows (for example "rejected by technical goes back to contracts") must be modelled as explicit step outcomes in our format; there is no general flowchart. That is enough for the chains the pilot needs.
- If a future need is a general graph workflow for integrations, Elsa (library mode, state in our row, proven in the spike) is the first candidate; Temporal remains as in ADR-0003.
- Updated with this decision: document 02 (stack row and section 5), document 05, document 06 section 6, document 07, document 09 (W-20 Done), document 10, README and CLAUDE.md. Document 03 never named Elsa.

## Alternatives considered

| Option | Why not now |
|---|---|
| Elsa 3 as executor and Studio as the tenant editor | Studio fails the Arabic and right-to-left test and reintroduces MudBlazor, Radzen and Monaco (docs/06 section 6.2). |
| Elsa 3 as executor only, our own editor | Works (spike checks pass), but Elsa adds nothing our linear step model needs, the invariants still live in our code, and running tenders depend on Elsa's JSON format across upgrades. |
| Elsa Studio for platform staff only, in English | The pilot has no editor (ADR-0003 point 4); a second UI stack for an internal screen is not worth it. |
