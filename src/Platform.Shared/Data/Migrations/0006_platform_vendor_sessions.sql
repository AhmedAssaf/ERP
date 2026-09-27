-- ADR-0012: vendor sessions are not staff in row-level security (pentest of the vendor slice, P-1).
-- A vendor signed in on a tenant host runs with app.tenant_id (the host tenant) and app.vendor_company_id. The tenant
-- policy of 0001 checked only the tenant, so inside a tenant a vendor session was indistinguishable from staff.
--
-- 1. platform.enable_tenant_rls becomes staff-only: tenant_id = current_tenant() and current_vendor_company() is null.
-- 2. platform.enable_tenant_vendor_rls(schema, table, company_column): tenant_id = current_tenant() and (no vendor
--    context, or the row's company column is the session's vendor company), under the policy name
--    tenant_vendor_isolation. Used only where a vendor reads its own rows inside a tenant (vendor.relationships today).
-- 3. Every table that already has the tenant policy is re-applied here with the staff-only rule. The shared platform
--    migrations run before every module's (MigrationRunner), so on a new database there is none yet and each module's
--    own first migration picks up the new rule; on an existing database this loop updates them. The modules then apply
--    their explicit exceptions in their own later migrations (audit 0002: vendors may insert into the tenant's log;
--    vendors 0013: vendor.relationships uses the vendor helper).

create or replace function platform.enable_tenant_rls(p_schema text, p_table text) returns void
    language plpgsql
as $$
begin
    execute format('alter table %I.%I enable row level security', p_schema, p_table);
    execute format('alter table %I.%I force row level security', p_schema, p_table);
    execute format('drop policy if exists tenant_isolation on %I.%I', p_schema, p_table);
    execute format('drop policy if exists tenant_vendor_isolation on %I.%I', p_schema, p_table);
    execute format(
        'create policy tenant_isolation on %I.%I '
        'using (tenant_id = platform.current_tenant() and platform.current_vendor_company() is null) '
        'with check (tenant_id = platform.current_tenant() and platform.current_vendor_company() is null)',
        p_schema, p_table);
end
$$;

create or replace function platform.enable_tenant_vendor_rls(p_schema text, p_table text, p_company_column text) returns void
    language plpgsql
as $$
begin
    execute format('alter table %I.%I enable row level security', p_schema, p_table);
    execute format('alter table %I.%I force row level security', p_schema, p_table);
    execute format('drop policy if exists tenant_isolation on %I.%I', p_schema, p_table);
    execute format('drop policy if exists tenant_vendor_isolation on %I.%I', p_schema, p_table);
    execute format(
        'create policy tenant_vendor_isolation on %I.%I '
        'using (tenant_id = platform.current_tenant() and (platform.current_vendor_company() is null or %I = platform.current_vendor_company())) '
        'with check (tenant_id = platform.current_tenant() and (platform.current_vendor_company() is null or %I = platform.current_vendor_company()))',
        p_schema, p_table, p_company_column, p_company_column);
end
$$;

-- Only the owner running migrations may change a table's policies.
revoke execute on function platform.enable_tenant_rls(text, text) from public;
revoke execute on function platform.enable_tenant_vendor_rls(text, text, text) from public;

-- Re-apply the staff-only rule to every table that has the tenant policy of 0001 today.
do $$
declare
    v_table record;
begin
    for v_table in
        select distinct p.schemaname, p.tablename
        from pg_policies p
        where p.policyname = 'tenant_isolation'
    loop
        perform platform.enable_tenant_rls(v_table.schemaname, v_table.tablename);
    end loop;
end
$$;
