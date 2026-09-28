-- The staff functions refuse a vendor context (QA of vendor plan task 5). vendor.related_company and
-- vendor.related_documents (0001) and vendor.related_companies and vendor.related_current_documents (0010) run as their
-- owner across the vendor-company policy and checked only the tenant, so a connection of a vendor on a tenant host
-- (app.vendor_company_id set) could read every company related to that tenant: competitors' CR numbers, contacts and
-- documents. They are the tenant staff's view; a vendor reads its own company under the company policy. Each now also
-- requires platform.current_vendor_company() is null. Same signatures and result columns, so CREATE OR REPLACE keeps the
-- grants; they are restated anyway. No new table, so no new row-level security policy.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create or replace function vendor.related_company(p_company_id uuid)
    returns table (
        id uuid, cr_number text, name_ar text, name_en text, vat_number text, address text,
        contact_name text, contact_phone text, contact_email text)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select c.id, c.cr_number, c.name_ar, c.name_en, c.vat_number, c.address, c.contact_name, c.contact_phone, c.contact_email
    from vendor.companies c
    where c.id = p_company_id
      and platform.current_vendor_company() is null
      and exists (select 1 from vendor.relationships r
                  where r.tenant_id = platform.current_tenant() and r.company_id = c.id)
$$;

create or replace function vendor.related_documents(p_company_id uuid)
    returns table (
        id uuid, type text, expires_on date, object_key text, sha256 text, scan_status text, is_current boolean,
        created_at timestamptz)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, d.type, d.expires_on, d.object_key, d.sha256, d.scan_status, d.is_current, d.created_at
    from vendor.documents d
    where d.company_id = p_company_id
      and d.scan_status = 'clean'
      and platform.current_vendor_company() is null
      and exists (select 1 from vendor.relationships r
                  where r.tenant_id = platform.current_tenant() and r.company_id = d.company_id)
$$;

create or replace function vendor.related_companies()
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
      and platform.current_vendor_company() is null
$$;

create or replace function vendor.related_current_documents()
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
      and platform.current_vendor_company() is null
      and exists (select 1 from vendor.relationships r
                  where r.tenant_id = platform.current_tenant() and r.company_id = d.company_id)
$$;

revoke all on function vendor.related_company(uuid) from public;
revoke all on function vendor.related_documents(uuid) from public;
revoke all on function vendor.related_companies() from public;
revoke all on function vendor.related_current_documents() from public;
grant execute on function vendor.related_company(uuid) to erp_app;
grant execute on function vendor.related_documents(uuid) to erp_app;
grant execute on function vendor.related_companies() to erp_app;
grant execute on function vendor.related_current_documents() to erp_app;
