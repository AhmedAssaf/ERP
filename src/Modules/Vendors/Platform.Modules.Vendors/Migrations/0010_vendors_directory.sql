-- The tenant's vendor list (vendor plan task 5, V-11): tenant staff see the companies their tenant has a relationship
-- with, and those companies' current clean documents, in one call each instead of one call per company. Like
-- related_company and related_documents (0001) they run as their owner across the vendor-company policy, so each filters
-- on platform.current_tenant() itself: no tenant, no rows. No new table, so no new row-level security policy.
-- join_tenant (0003) now answers whether it created the relationship, so a join is audited once even when two requests
-- of the same vendor join at the same moment.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

-- The companies related to the connection's tenant, with the tenant's relationship to each.
create function vendor.related_companies()
    returns table (
        id uuid, cr_number text, name_ar text, name_en text, status text, first_seen_at timestamptz, approved_by text)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select c.id, c.cr_number, c.name_ar, c.name_en, r.status, r.first_seen_at, r.approved_by
    from vendor.relationships r
    join vendor.companies c on c.id = r.company_id
    where r.tenant_id = platform.current_tenant()
$$;

-- The current clean document of each type, for every company related to the connection's tenant (V-10: a pending or
-- infected file is never handed to a tenant).
create function vendor.related_current_documents()
    returns table (company_id uuid, type text, expires_on date)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.company_id, d.type, d.expires_on
    from vendor.documents d
    where d.is_current
      and d.scan_status = 'clean'
      and exists (select 1 from vendor.relationships r
                  where r.tenant_id = platform.current_tenant() and r.company_id = d.company_id)
$$;

-- A vendor meets another tenant (the join flow, vendor plan task 5): unchanged from 0003 except that it returns true when
-- this call created the pending relationship and false when one existed already (an approved one stays approved).
drop function vendor.join_tenant();

create function vendor.join_tenant()
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_tenant  uuid := platform.current_tenant();
    v_company uuid := platform.current_vendor_company();
    v_user    text := platform.current_user_id();
begin
    if v_tenant is null or v_company is null or v_user is null
       or not exists (select 1 from vendor.vendor_users u where u.company_id = v_company and u.user_id = v_user) then
        raise exception 'A vendor joins a tenant as a signed-in user of its company, on that tenant''s host.'
            using errcode = 'insufficient_privilege';
    end if;

    insert into vendor.relationships (tenant_id, company_id, status)
    values (v_tenant, v_company, 'pending')
    on conflict (tenant_id, company_id) do nothing;
    return found;
end
$$;

revoke all on function vendor.join_tenant() from public;
grant execute on function vendor.join_tenant() to erp_app;
revoke all on function vendor.related_companies() from public;
revoke all on function vendor.related_current_documents() from public;
grant execute on function vendor.related_companies() to erp_app;
grant execute on function vendor.related_current_documents() to erp_app;
