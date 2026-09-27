-- Pentest P-9 and P-13 (ADR-0012 point 4): the tenancy functions state who may call them.
-- 1. tenancy.update_branding changed the row of platform.current_tenant() for any caller. It now refuses a vendor
--    context and requires the acting user to be an active tenant admin of that tenant in identity.members (the Identity
--    module's table, read here as the function's owner; plpgsql resolves it when the function runs, after the Identity
--    migrations). Without a tenant it still returns no row.
-- 2. tenancy.list_tenants is the platform console's: it refuses a session with a tenant or vendor context.
-- Same signatures and result columns; the grants are restated.

create or replace function tenancy.update_branding(p_portal_name text, p_primary_color text, p_logo_url text)
    returns table (portal_name text, primary_color text, logo_url text, hosts text[])
    language plpgsql
    volatile
    security definer
    set search_path = tenancy, pg_temp
as $$
#variable_conflict use_column
declare
    v_tenant uuid := platform.current_tenant();
begin
    if v_tenant is null then
        return;
    end if;

    if platform.current_vendor_company() is not null
       or not exists (select 1 from identity.members m
                      where m.tenant_id = v_tenant
                        and m.user_id = platform.current_user_id()
                        and m.status = 'active'
                        and 'tenant-admin' = any (m.roles)) then
        raise exception 'A tenant is branded by one of its active tenant admins.' using errcode = 'insufficient_privilege';
    end if;

    return query
        update tenancy.tenants t
           set portal_name   = coalesce(p_portal_name, t.portal_name),
               primary_color = coalesce(p_primary_color, t.primary_color),
               logo_url      = coalesce(p_logo_url, t.logo_url)
         where t.id = v_tenant
        returning t.portal_name, t.primary_color, t.logo_url,
                  array(select h.host from tenancy.tenant_hosts h where h.tenant_id = t.id order by h.host);
end
$$;

create or replace function tenancy.list_tenants()
    returns table (tenant_id uuid, slug text, keycloak_org_alias text, portal_name text, status text)
    language plpgsql
    stable
    security definer
    set search_path = tenancy, pg_temp
as $$
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null then
        raise exception 'The tenant list is the platform console''s.' using errcode = 'insufficient_privilege';
    end if;

    return query
        select t.id, t.slug, t.keycloak_org_alias, t.portal_name, 'active'::text
        from tenancy.tenants t
        order by t.portal_name, t.slug;
end
$$;

revoke all on function tenancy.update_branding(text, text, text) from public;
revoke all on function tenancy.list_tenants() from public;
grant execute on function tenancy.update_branding(text, text, text) to erp_app;
grant execute on function tenancy.list_tenants() to erp_app;
