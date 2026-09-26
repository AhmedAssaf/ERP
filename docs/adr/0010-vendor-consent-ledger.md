# ADR-0010: Vendor consent ledger before any vendor data leaves the platform

Date: 2026-09-26
Status: Accepted
Deciders: Ahmed Assaf
Related: F-64, F-65, F-10, N-02, N-11; docs/12 section 7 choice 3; ADR-0008, ADR-0009

## Context

With one vendor identity across tenants (ADR-0008) and signed award records (ADR-0009), a vendor's award and PO history becomes valuable to third parties such as a financier. The PDPL requires consent that is specific, recorded, and revocable before personal or business data goes to another party. The user chose on 2026-09-26 to build the ledger in the MVP so the rule exists before any integration does.

## Decision

1. A vendor admin grants consent to a named recipient for a named scope (for example award records, PO records, profile documents) and a period. The grant is a row; revoking it is a new row. Rows are append-only and audited.
2. Every export of vendor data to a party other than the vendor itself and the tenant it contracted with checks the ledger at the moment of export, and the export is written to the audit log with the grant it relied on.
3. In the MVP there are no external recipients yet. The MVP ships the ledger, the vendor screen to grant, view and revoke, and the check as the only path any later export may use.
4. A tenant cannot grant consent on a vendor's behalf, and no default grant exists.

## Consequences

- The legal basis for a future financier partner exists before the partner does.
- The MVP carries a feature with no external user until stage 3 of path C; kept small (one table, one screen, one check).
- Changes: docs/02 F-64 and N-02; docs/05 MVP rows; docs/09 F-64.

## Alternatives considered

| Option | Why not now |
|---|---|
| Reserve the feature ID, build at stage 3 | Recommended first; the user chose to build it in the MVP |
| Consent inside the platform terms of service | Not specific or revocable enough for the PDPL |
