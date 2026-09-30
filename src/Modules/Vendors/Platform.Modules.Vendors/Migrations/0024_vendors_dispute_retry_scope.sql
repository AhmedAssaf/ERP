-- W-33 review of PR #5 (2026-09-30), two blocking findings:
--
-- 1. A retry of an uphold's identity provider update must never undo a tenant's own decision (W-21 P-1: only the tenant
--    restores access). The application now reruns only the steps that failed; here:
--    - vendor.dispute_related_tenants names only the tenants the company worked with when the uphold committed
--      (first_seen_at at or before resolved_at). A relationship created later is the claimant's own join, which gave them
--      that tenant's membership itself; a retry must not put them back after that tenant removed them.
--    - vendor.supersede_dispute_idp marks an upheld dispute whose claimant is no longer the company's vendor admin (a later
--      dispute moved the company on) as superseded, so its retry grants nothing and it leaves the console's list. The
--      check and the mark are one statement, after the company row is locked FOR SHARE like the other decisions about one
--      company (0019 L-1), so a competing uphold either waits for it or is seen by it.
--    - idp_outcome may be 'superseded'; vendor.upheld_disputes_needing_idp lists only a missing or failed outcome.
-- 2. vendor.claimant_awaiting_organization (0023) answered true when the organization's key was simply missing from
--    idp_details, so a claimant removed by a tenant the uphold never covered (related after it, or skipped because the
--    catalog did not list it) was told "still giving you access" forever instead of W-21's audited refusal. It is now
--    true only when the outcome was never recorded (idp_details is null), the add to that organization failed, or the
--    lookup of the related tenants failed (so no organization was tried).
--
-- No new table, so no new row-level security policy; cr_disputes keeps its forced policy of 0019. Every function keeps
-- its caller check (ADR-0012 point 4) and is executable by erp_app only.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

alter table vendor.cr_disputes drop constraint ck_cr_disputes_idp_outcome;
alter table vendor.cr_disputes
    add constraint ck_cr_disputes_idp_outcome check (
        (idp_outcome is null or idp_outcome in ('updated', 'failed', 'superseded')) and (idp_outcome is null) = (idp_recorded_at is null));

-- Finding 2.
create or replace function vendor.claimant_awaiting_organization(p_organization_alias text)
    returns boolean
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select exists (
        select 1 from vendor.cr_disputes d
        where d.status = 'upheld'
          and d.claimant_user_id = platform.current_user_id()
          and d.company_id = platform.current_vendor_company()
          and platform.current_user_id() is not null
          and platform.current_vendor_company() is not null
          and (d.idp_details is null
               or d.idp_details ->> ('organization:add:' || p_organization_alias) = 'failed'
               or d.idp_details ->> 'organization:lookup' = 'failed'))
$$;

-- Finding 1: the tenants of the uphold, not of today.
create or replace function vendor.dispute_related_tenants(p_dispute_id uuid)
    returns table (tenant_id uuid)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select r.tenant_id
    from vendor.cr_disputes d
    join vendor.relationships r on r.company_id = d.company_id
    where d.id = p_dispute_id
      and d.status = 'upheld'
      and r.first_seen_at <= d.resolved_at
      and platform.current_tenant() is null
      and platform.current_vendor_company() is null
      and platform.current_user_id() is not null
    order by r.tenant_id
$$;

create or replace function vendor.upheld_disputes_needing_idp()
    returns table (
        id uuid, company_id uuid, cr_number text, name_ar text, name_en text, claimant_user_id text, claimant_name text,
        removed_user_ids text[], resolved_at timestamptz, idp_outcome text, idp_details jsonb)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, d.company_id, c.cr_number, c.name_ar, c.name_en, d.claimant_user_id, d.claimant_name,
           d.removed_user_ids, d.resolved_at, d.idp_outcome, d.idp_details
    from vendor.cr_disputes d
    join vendor.companies c on c.id = d.company_id
    where d.status = 'upheld'
      and (d.idp_outcome is null or d.idp_outcome = 'failed')
      and platform.current_tenant() is null
      and platform.current_vendor_company() is null
      and platform.current_user_id() is not null
    order by d.resolved_at, d.id
$$;

-- Finding 1: true when the dispute needed a retry and its claimant is no longer the company's vendor admin; the dispute
-- is then marked superseded (its stored steps kept). False, and nothing changed, otherwise.
create function vendor.supersede_dispute_idp(p_dispute_id uuid)
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_company uuid;
begin
    perform vendor.require_platform_admin_session();
    select d.company_id into v_company from vendor.cr_disputes d where d.id = p_dispute_id;
    if not found then
        return false;
    end if;

    perform 1 from vendor.companies c where c.id = v_company for share;

    update vendor.cr_disputes d
    set idp_outcome = 'superseded', idp_recorded_at = now()
    where d.id = p_dispute_id
      and d.status = 'upheld'
      and (d.idp_outcome is null or d.idp_outcome = 'failed')
      and not exists (
          select 1 from vendor.vendor_users u
          where u.company_id = d.company_id and u.user_id = d.claimant_user_id and u.role = 'vendor-admin');
    return found;
end
$$;

revoke all on function vendor.claimant_awaiting_organization(text) from public;
revoke all on function vendor.dispute_related_tenants(uuid) from public;
revoke all on function vendor.upheld_disputes_needing_idp() from public;
revoke all on function vendor.supersede_dispute_idp(uuid) from public;
grant execute on function vendor.claimant_awaiting_organization(text) to erp_app;
grant execute on function vendor.dispute_related_tenants(uuid) to erp_app;
grant execute on function vendor.upheld_disputes_needing_idp() to erp_app;
grant execute on function vendor.supersede_dispute_idp(uuid) to erp_app;
