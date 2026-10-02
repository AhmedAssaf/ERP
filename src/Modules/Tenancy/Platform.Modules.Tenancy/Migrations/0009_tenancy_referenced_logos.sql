-- W-38: the logo cleanup job (BrandingLogoCleanupJob) deletes logo objects that no tenant references. It has no tenant
-- context, so it reads every tenant's logo_url through this security-definer function (the table itself is not granted
-- to erp_app), which refuses a session with a tenant or vendor context, as tenancy.list_tenants does since 0007. It
-- returns the stored logo URLs only: no names, colours or hosts. No new table, so no new row-level security policy.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the tenancy migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create function tenancy.referenced_logos()
    returns table (tenant_id uuid, logo_url text)
    language plpgsql
    stable
    security definer
    set search_path = tenancy, pg_temp
as $$
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null then
        raise exception 'The referenced logos are listed for platform jobs only.' using errcode = 'insufficient_privilege';
    end if;

    return query
        select t.id, t.logo_url
        from tenancy.tenants t
        where t.logo_url is not null;
end
$$;

revoke all on function tenancy.referenced_logos() from public;
grant execute on function tenancy.referenced_logos() to erp_app;
