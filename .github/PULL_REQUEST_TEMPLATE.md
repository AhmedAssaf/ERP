## What

One or two sentences. Feature or requirement IDs: F-xx / N-xx. Plan task: `docs/superpowers/plans/<file>` task N (if any).

## Why

Link the issue. If this changes a decision, link the ADR.

## Evidence

- Tests added or changed:
- `dotnet test` output (paste the summary line):
- reviewer agent verdict (paste):
- qa-engineer agent result table (paste, if a feature ID is being closed):

## Invariants checked

- [ ] Tenant isolation (F-05): new tables carry `TenantId` and an RLS policy, or no tables added
- [ ] Sealed envelopes and locking (F-23, F-30): untouched or re-tested
- [ ] Deadline uses server time (F-24): untouched or re-tested
- [ ] Audit log append-only (F-41): untouched or re-tested
- [ ] Arabic and English resources updated; RTL checked on changed screens (F-04)

## Docs

- [ ] Docs and diagrams updated where behaviour they describe changed, or nothing to update
