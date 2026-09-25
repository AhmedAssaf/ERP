# ADR-0003: Build the tender workflow as tenant-configurable data from day one

Date: 2026-09-21
Status: Accepted
Deciders: Ahmed Assaf
Related: F-08, F-09, F-27, F-33, F-56, W-20; `docs/02` section 2.6 and 4.4; `docs/03` section 7

## Context

Document 02 hard-coded one evaluation chain (contracts screening, technical scoring, financial opening, finance approval) and listed a workflow engine as a later option "if tenants need to design approval flows". The user's requirement is that each customer defines which departments take part, in what order, and who can approve with what limits, and that this must not require a migration or refactor after the pilot. Retrofitting a configurable chain onto a hard-coded state machine is exactly such a refactor: every stage transition, permission check, and audit row would change.

## Decision

1. **Workflow definitions are first-class from the foundation slice.** A tenant owns one or more workflow definitions. A definition is an ordered list of steps; each step names a department, the role or named users who act, the rule (any-of or all-of), optional amount thresholds, and the system stage it belongs to (screening, scoring, locking, financial opening, approval, award). Templates ship for the common chains.
2. **Every tender takes a snapshot of its definition at publishing.** The tender's state machine (F-27) executes the snapshot, never the live definition, so editing a definition cannot alter a running tender.
3. **The invariants stay outside the workflow.** Sealed envelopes (F-23), score locking before financial opening (F-30), deadline enforcement (F-24), and append-only audit (F-41) are enforced by the domain, and a definition cannot reorder or skip them. A definition only decides who acts between those fixed points.
4. **The pilot uses the default template with no editor screen.** The model and executor ship in the MVP (F-56); the tenant-facing editor follows in version 1.1 (F-56b).
5. **The execution engine is decided by a spike before the foundation is built (W-20).** The candidate is Elsa 3. The spike answers whether custom activities can guarantee point 3 and whether the designer can be delivered in Arabic under the tenant's brand. If yes, Elsa executes the snapshot; if no, our state machine does. The definition model in point 1 is the same either way, which is what makes the choice reversible without a migration.

## Consequences

- The foundation slice grows by one model and one executor. Estimated one additional week on the kickoff plan.
- F-08 per-tender committees and F-09 delegation of authority become configuration of steps, not separate features; their acceptance criteria move under F-56.
- The data model gains three tables (document 03 section 7): workflow definition, workflow step, and the tender's workflow snapshot with step outcomes.
- The Elsa spike is on the critical path of kickoff. If it overruns its one-week box, the state-machine executor is chosen and Elsa is closed as an option for version 1.
- Document 07 section 10 no longer says "no workflow engine"; it says the engine is chosen by W-20 and the model is engine-agnostic.

## Alternatives considered

| Option | Why not |
|---|---|
| Fixed chain for the pilot, configurable chain later | The stated requirement forbids the later refactor. |
| Adopt Elsa outright without a spike | Elsa Studio is built on MudBlazor and is left-to-right English; the invariants would depend on custom activities that have not been proven. A spike is cheaper than discovering this in week six. |
| Full BPMN engine (Camunda, Zeebe) | Java or a separate service; wrong shape for a .NET modular monolith and a two-person team. |
| Temporal (durable execution, added 2026-09-26) | Built for high-throughput, code-defined workflows across services; ours are low-volume, data-defined, and wait days on human approvers. It keeps a second copy of tender state outside PostgreSQL row-level security and the append-only audit (F-41), needs a self-hosted cluster to keep data in Saudi Arabia, and still leaves the snapshot interpreter (F-56) to us. Revisit only if the monolith splits into services or integration sagas appear (PO push to a customer ERP, e-signature F-36b, payment), and then for those sagas, not the tender workflow. |
