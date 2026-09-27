-- Review of the vendor slice (2026-09-28).
-- 1. Owner guard. tenancy.update_branding reads identity.members (forced row-level security) as its owner, so this
--    module's migrations must run as a role with BYPASSRLS (or the superuser), as the vendors migrations always check.
--    0007 lacked the check and is applied and checksummed, so it is made here, for the current role and for the owner of
--    every security-definer function this module already has.
-- 2. tenancy.update_branding also refuses an acting user who is a vendor user, with or without a vendor context (the
--    rule vendor.approve_relationship follows since vendors 0014). A tenant admin who is also a vendor user was refused
--    in a circuit (vendor context set) but allowed on the logo POST (none); both paths now answer the same.
--    vendor.vendor_users belongs to the Vendors module, whose migrations run after this one; plpgsql resolves the name
--    when the function runs. Same signature and result columns; the grants are restated.
-- 3. The comment on tenancy.list_tenants (0005) said the database cannot tell a console request from a tenant request;
--    since 0007 it refuses a session with a tenant or vendor context, so the comment is restated.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the tenancy migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;

    if exists (select 1
               from pg_proc p
               join pg_namespace n on n.oid = p.pronamespace
               join pg_roles r on r.oid = p.proowner
               where n.nspname = 'tenancy' and p.prosecdef and not (r.rolsuper or r.rolbypassrls)) then
        raise exception 'A tenancy security-definer function is owned by a role without BYPASSRLS; reassign it to the migration owner before migrating.';
    end if;
end
$$;

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
    v_user   text := platform.current_user_id();
begin
    if v_tenant is null then
        return;
    end if;

    if platform.current_vendor_company() is not null
       or v_user is null
       or exists (select 1 from vendor.vendor_users u where u.user_id = v_user)
       or not exists (select 1 from identity.members m
                      where m.tenant_id = v_tenant
                        and m.user_id = v_user
                        and m.status = 'active'
                        and 'tenant-admin' = any (m.roles)) then
        raise exception 'A tenant is branded by one of its active tenant admins, never by a vendor user.' using errcode = 'insufficient_privilege';
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

revoke all on function tenancy.update_branding(text, text, text) from public;
grant execute on function tenancy.update_branding(text, text, text) to erp_app;

comment on function tenancy.list_tenants() is
    'Every tenant, for the platform console only (F-54). Executable by erp_app, the role every request connects as. Since '
    'tenancy migration 0007 it refuses (42501) a session with a tenant or vendor context, so a tenant-host request cannot '
    'list tenants. A session with neither (the platform host, and the worker) is not told apart here: ITenantCatalog also '
    'refuses unless the scope is a platform request, and the console pages require the PlatformAdmin policy (ADR-0012 point 4).';
