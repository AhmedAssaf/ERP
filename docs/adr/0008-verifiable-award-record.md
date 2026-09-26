# ADR-0008: Issue a signed, verifiable award record from the audit chain

Date: 2026-09-26
Status: Accepted
Deciders: Ahmed Assaf
Related: F-36, F-41, F-42, F-64; docs/12 section 7 choice 2; ADR-0009

## Context

The audit log (F-41) is append-only and hashed, but its evidence can only be read inside WaslaBid. An auditor, a vendor's bank, or a financier (path C, docs/12) needs to confirm an award without an account: who awarded what to whom, when, and that the tender history behind it was not edited. Designing this together with the audit bundle (F-42), which the MVP now includes, costs little; changing the audit format after real tenders have run costs more.

## Decision

1. When a tender is awarded and when its PO is issued, the platform writes an award record: tenant, vendor CR number, tender reference, amount with VAT, currency, date, PO number when present, and the hash of the tender's audit chain at that moment.
2. The record is signed with a WaslaBid platform key held in the KMS (N-10). The public key is published so anyone can verify a record offline, and a verification page accepts a record and says valid or not valid without showing anything else.
3. The record is shown in the F-42 audit bundle and given to the awarded vendor. It never contains offer content, scores, or other vendors' data.
4. Sharing the record with a third party through the platform needs the vendor's consent (F-63, ADR-0009). A vendor may still hand its own copy to anyone.

## Consequences

- Auditors can check an award without logging in, which strengthens the governance sale (docs/11 section 2).
- A future financier gets underwriting evidence without WaslaBid holding or moving funds (N-11).
- Key rotation needs a key identifier in each record and old public keys kept published.
- Changes: docs/02 F-64, F-42; docs/05 MVP rows; docs/09 F-64.

## Alternatives considered

| Option | Why not now |
|---|---|
| Plain audit hashes only, signed records later | Changing the audit format after live tenders means re-signing or two formats |
| Blockchain anchoring | Adds a dependency and cost with no buyer asking for it; a published key gives the same verification |
