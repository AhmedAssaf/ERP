-- F-02 branding writes (spec D-9, section 4.3; plan task 10). erp_app still has no table rights on tenancy.tenants: it
-- writes through tenancy.update_branding, which changes only the row of the connection's tenant
-- (platform.current_tenant(), the same app.tenant_id setting row-level security reads), so a tenant can brand only itself.

-- The logo may now also be our own logo route: a SHA-256 in lower-case hex and .png, nothing else (no dots, slashes or
-- markup characters can pass), so the value still cannot become a javascript: or data: URL or break out of an attribute.
alter table tenancy.tenants drop constraint ck_tenants_logo_url;
alter table tenancy.tenants
    add constraint ck_tenants_logo_url check (
        logo_url is null
        or logo_url ~ '^https://[^\s"''()<>]+$'
        or logo_url ~ '^/branding/logo/[a-f0-9]{64}\.png$');

-- The portal name is shown in every tenant page header: one to a hundred characters, not only spaces.
alter table tenancy.tenants
    add constraint ck_tenants_portal_name check (char_length(portal_name) between 1 and 100 and btrim(portal_name) <> '');

-- A null argument keeps the stored value, so the name and colour, and the logo, are saved separately. Returns the stored
-- branding and the tenant's host names (the application drops them from its host cache), or no row without a tenant.
create function tenancy.update_branding(p_portal_name text, p_primary_color text, p_logo_url text)
    returns table (portal_name text, primary_color text, logo_url text, hosts text[])
    language sql
    volatile
    security definer
    set search_path = tenancy, pg_temp
as $$
    update tenancy.tenants t
       set portal_name   = coalesce(p_portal_name, t.portal_name),
           primary_color = coalesce(p_primary_color, t.primary_color),
           logo_url      = coalesce(p_logo_url, t.logo_url)
     where t.id = platform.current_tenant()
    returning t.portal_name, t.primary_color, t.logo_url,
              array(select h.host from tenancy.tenant_hosts h where h.tenant_id = t.id order by h.host)
$$;

revoke all on function tenancy.update_branding(text, text, text) from public;
grant execute on function tenancy.update_branding(text, text, text) to erp_app;
