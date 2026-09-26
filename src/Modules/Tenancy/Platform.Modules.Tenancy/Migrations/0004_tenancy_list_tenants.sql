-- The platform console's tenant list (F-54, spec 3.4). erp_app still has no table rights on tenancy.tenants; it reads
-- through this security-definer function, which the application calls only after the PlatformAdmin policy passed.
-- Status is 'active' for every tenant until suspension exists (F-54 full). The organization alias is returned so the
-- console can count organization members in Keycloak.
create function tenancy.list_tenants()
    returns table (tenant_id uuid, slug text, keycloak_org_alias text, portal_name text, status text)
    language sql
    stable
    security definer
    set search_path = tenancy, pg_temp
as $$
    select t.id, t.slug, t.keycloak_org_alias, t.portal_name, 'active'::text
    from tenancy.tenants t
    order by t.portal_name, t.slug
$$;

revoke all on function tenancy.list_tenants() from public;
grant execute on function tenancy.list_tenants() to erp_app;
