# ADR-0012: Keep vendor sessions out of staff rows in row-level security

Date: 2026-09-28
Status: Accepted 2026-09-28
Deciders: Ahmed Assaf
Related: N-02 tenant isolation, F-10, F-23, F-41, ADR-0008, vendor slice spec `docs/superpowers/specs/2026-09-27-vendors-design.md`, pentest of the vendor slice 2026-09-28 (P-1 to P-3)

## Context

A vendor signed in on a tenant host runs with both `app.tenant_id` (the host tenant) and `app.vendor_company_id` set. The tenant policy created by `platform.enable_tenant_rls` checks only `tenant_id = platform.current_tenant()`, so inside that tenant the database cannot tell a vendor from staff. The pentest proved that a vendor session can read every relationship row of the tenant (competitors' company ids and status), the tenant's staff members and audit events, and can insert an `identity.members` row for itself. None of this is reachable through the app today, because the web policies keep vendors off those paths, but the database is meant to hold isolation on its own, and the first tenant table holding offers (F-23) would make the same gap critical. Migration 0011 already closed the same gap for four security-definer functions.

## Decision

1. **Staff-only by default.** `platform.enable_tenant_rls` creates `tenant_id = platform.current_tenant() and platform.current_vendor_company() is null` for both `using` and `with check`. Every existing tenant table is re-applied by a migration, and new tables get it automatically.
2. **Vendor-visible tenant tables are explicit.** A second helper, `platform.enable_tenant_vendor_rls(schema, table, company_column)`, creates `tenant_id = platform.current_tenant() and (platform.current_vendor_company() is null or <company_column> = platform.current_vendor_company())`. It is used only where a vendor legitimately reads its own row inside a tenant: today `vendor.relationships`; later a vendor's own offers and the tenders it is invited to (with their own rules for sealed envelopes).
3. **Audit is write-only for vendors.** `audit.events` keeps one policy for insert that accepts a vendor context (vendor actions are audited in the tenant's log, F-41) and a staff-only policy for select.
4. **Security-definer functions state who may call them.** Staff functions refuse a vendor context (as 0011 does) and, where they change a decision, check the acting user's role in `identity.members`; vendor functions refuse a missing or different vendor context; and worker-only functions refuse any tenant or vendor context, so a tenant-host session cannot call them. Until a separate worker role exists (see Consequences), "no tenant and no vendor context" also matches every platform-host session, so a platform admin's session can call the worker's functions and read the platform audit; the platform host serves only platform admins behind the PlatformAdmin policy with OTP, and a test pins this known gap so it is closed deliberately. The platform audit is written only through a security-definer function and read only without a tenant or vendor context.
5. **Tests.** The table catalog test asserts that every tenant table uses one of the two policies, and the pentest tests in `tests/Platform.IntegrationTests/Security/` stay as regression tests.

## Consequences

- The database holds the vendor-versus-staff line inside a tenant by itself, so a query that forgets a company filter cannot leak a competitor's rows.
- Every future tenant table that a vendor must see needs a deliberate choice of the second helper, which a reviewer can check in the migration.
- One migration touches tables in the Identity, Audit, Workflow and Vendors schemas; it runs in the vendor slice before the slice is merged.
- A separate database role for the worker (so worker-only functions need no context rule) stays a later option.
- docs/03 diagram 5 (tenant isolation) gains the vendor context as a second key when the vendor slice updates its docs.

## Alternatives considered

| Option | Why not now |
|---|---|
| Leave `app.tenant_id` empty for vendor sessions | The vendor's pages are tenant-branded and its relationship, audit and future offer rows are tenant rows; every vendor query would need a security-definer function |
| Separate database roles for staff and vendor connections | Two connection strings and pools per host for the same result; can follow later if the policies grow complex |
| Rely on the web policies and `VendorDirectory` checks | Isolation must not depend on every future query remembering a filter; that is the reason row-level security is forced |
