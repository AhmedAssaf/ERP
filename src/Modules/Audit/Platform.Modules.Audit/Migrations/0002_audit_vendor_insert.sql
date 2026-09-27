-- ADR-0012 point 3: the tenant's audit log is write-only for vendors. A vendor's own actions on a tenant host (joining
-- the tenant, restoring its organization membership) are audited in that tenant's log (F-41), written in the vendor's
-- session, so inserting accepts a vendor context; reading stays staff-only, so a vendor session never reads the tenant's
-- log. The application role holds only SELECT and INSERT on the table (0001), so no UPDATE or DELETE policy is needed.
-- Replaces the single policy that platform.enable_tenant_rls (platform migration 0006) wrote for all commands.

alter table audit.events enable row level security;
alter table audit.events force row level security;
drop policy if exists tenant_isolation on audit.events;
drop policy if exists tenant_audit_insert on audit.events;

create policy tenant_isolation on audit.events
    for select
    using (tenant_id = platform.current_tenant() and platform.current_vendor_company() is null);

create policy tenant_audit_insert on audit.events
    for insert
    with check (tenant_id = platform.current_tenant());
