-- W-33 third review (2026-09-30): 0020 ordered the console's list by the stored over_cap flag across every company, and
-- the flag, set once when a dispute was raised, stayed set after the disputes before it were closed, so the real owner's
-- dispute could stay at the bottom and badged after the throwaway ones were rejected. The list is now grouped by company
-- (the company with the oldest pending dispute first), oldest first within the company, and over_cap is worked out when
-- listing: a dispute is over the cap while five older disputes of its company are still pending. The stored column stays
-- as a record of what the company looked like when the dispute was raised. Same result columns as 0020, so CREATE OR
-- REPLACE keeps the grants.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create or replace function vendor.open_cr_disputes()
    returns table (
        id uuid, company_id uuid, cr_number text, name_ar text, name_en text,
        claimant_user_id text, claimant_email text, claimant_name text, statement text,
        raised_on_tenant uuid, raised_at timestamptz, registrant_user_id text, ownership_method text,
        status text, reviewed_at timestamptz, over_cap boolean, company_pending integer)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    with pending as (
        select d.*,
               row_number() over (partition by d.company_id order by d.raised_at, d.id) as company_rank,
               count(*) over (partition by d.company_id) as company_count,
               min(d.raised_at) over (partition by d.company_id) as company_first
        from vendor.cr_disputes d
        where d.status in ('open', 'under_review')
    )
    select p.id, p.company_id, c.cr_number, c.name_ar, c.name_en,
           p.claimant_user_id, p.claimant_email, p.claimant_name, p.statement,
           p.raised_on_tenant, p.raised_at, vendor.company_registrant(c.id), v.method,
           p.status, p.reviewed_at, p.company_rank > 5, p.company_count::integer
    from pending p
    join vendor.companies c on c.id = p.company_id
    left join vendor.ownership_verifications v on v.company_id = p.company_id
    where platform.current_tenant() is null
      and platform.current_vendor_company() is null
      and platform.current_user_id() is not null
    order by p.company_first, p.company_id, p.raised_at, p.id
$$;

revoke all on function vendor.open_cr_disputes() from public;
grant execute on function vendor.open_cr_disputes() to erp_app;
