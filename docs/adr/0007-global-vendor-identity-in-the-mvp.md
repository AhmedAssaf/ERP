# ADR-0007: One vendor identity across all tenants, from the MVP

Date: 2026-09-26
Status: Accepted
Deciders: Ahmed Assaf
Related: F-10, F-11, F-14, F-14a, F-19, F-19b, F-55, F-62, F-05; docs/02 section 5 item 2; docs/12 section 7 choice 1; ADR-0008, ADR-0009

## Context

Document 02 has always described one vendor account across tenants (F-10), but the MVP (docs/05 section 8) narrowed it to one account per vendor on the pilot tenant and left open decision 2 unresolved. Document 12 lists a global vendor identity as the first choice that is cheap now and expensive to retrofit: the vendor network and any later financing depend on it, and merging duplicate vendor accounts after real tenders have run means resolving conflicting contacts and documents by hand. The decision was reviewed with the user on 2026-09-26.

## Decision

1. A vendor company exists once on the platform, keyed by its CR number, which is mandatory and unique. Vendor company, vendor users, and vendor documents are platform-level rows in the Vendors module, without `tenant_id`.
2. Everything a tenant knows or decides about a vendor lives in a tenant-scoped relationship row under forced row-level security: approval state (pending, approved, blocked), address book entry (F-14a), tags, notes, and later scores and history. One tenant never sees another tenant's relationship row or learns that it exists.
3. Keycloak keeps the model in docs/02 section 4.3: vendor users live in the tenant realm with the `vendor` role and become members of each tenant organization with which their company has a relationship. The host still resolves the tenant and must match the token's organization.
4. Who may see a tender is set per tender (F-19): Invited (only invitees, F-55) or Open (listed on the tenant's public page, any registered vendor may join, F-19b). Both ship in the MVP.
5. A vendor may invite a company it sells to onto the platform as a prospective buyer (F-62). The invitation reaches WaslaBid sales; it never creates a tenant by itself.
6. This moves F-10 and F-19b into the MVP. The Vendors module is created by the vendor slice from its own spec and plan; the foundation needs no change beyond what that spec lists.

## Consequences

- A vendor registers and uploads its CR and certificates once and uses them with every buyer, which is the start of the vendor network (docs/11 section 7).
- Isolation has two shapes in one module: shared vendor facts and private tenant relationships. Tests must show that tenant B never reads tenant A's relationship rows and that a vendor document is visible to a tenant only while a relationship exists.
- Shared facts must stay facts the vendor itself provides. A tenant's judgement of a vendor never leaks into the shared profile.
- The MVP grows by about one to two weeks (F-10, F-19b).
- Changes: docs/02 F-10 acceptance and section 5 item 2; docs/05 rows and out-of-scope list; docs/09 F-10, F-19b, F-62; CLAUDE.md decisions.

## Alternatives considered

| Option | Why not now |
|---|---|
| One vendor account per tenant | Simplest isolation, but no network effect, and path C (docs/12) is blocked |
| Per-tenant in the MVP with a global-shaped schema, switch later | Recommended first on 2026-09-26; the user chose global from the start so the pilot vendors are never migrated |
| Vendors in a separate Keycloak realm | Cleaner separation, but a vendor would need a second login on every tenant host and organization membership would no longer carry the tenant |
