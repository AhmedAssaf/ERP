-- Platform-wide helpers used by every module's migrations (spec section 2.2).
create schema if not exists platform;

-- The tenant of the current connection, or NULL when none is set. NULLIF turns the empty string that a reset
-- custom setting returns into NULL, so "no tenant" means zero rows instead of a uuid cast error.
create or replace function platform.current_tenant() returns uuid
    language sql
    stable
as $$ select nullif(current_setting('app.tenant_id', true), '')::uuid $$;

-- Forced row-level security with one isolation policy for a tenant-owned table.
create or replace function platform.enable_tenant_rls(p_schema text, p_table text) returns void
    language plpgsql
as $$
begin
    execute format('alter table %I.%I enable row level security', p_schema, p_table);
    execute format('alter table %I.%I force row level security', p_schema, p_table);
    execute format('drop policy if exists tenant_isolation on %I.%I', p_schema, p_table);
    execute format(
        'create policy tenant_isolation on %I.%I using (tenant_id = platform.current_tenant()) with check (tenant_id = platform.current_tenant())',
        p_schema, p_table);
end
$$;

grant usage on schema platform to erp_app;
grant execute on function platform.current_tenant() to erp_app;
