-- W-33 third review of PR #5 (2026-09-30):
--
-- P-R2. vendor.dispute_claimant_is_admin (0025) asked whether the dispute's claimant is the company's vendor admin now,
--       not whether they stayed it since the dispute. X wins the company (dispute 1, the add to beta fails), loses it to Y
--       (dispute 2), wins it back (dispute 3, the add to beta works), and beta removes X: dispute 1's retry was not
--       superseded, its failed add ran, and X was back in beta against beta's decision (W-21 P-1). A dispute is now
--       superseded once a later upheld dispute of the same company exists (resolved_at after its own), whoever holds the
--       company: the later uphold decided the company's access for its own time. Upholds of one company are ordered: an
--       uphold closes the company's other pending disputes (0019), so a later upheld dispute was raised after the earlier
--       one committed and its resolved_at is later.
--       - vendor.dispute_claimant_is_admin: true only when the claimant is the company's vendor admin and no later
--         upheld dispute of the company exists.
--       - vendor.supersede_dispute_idp: marks when the claimant is not the vendor admin or a later upheld dispute exists.
--       - vendor.claimant_awaiting_organization: only the company's latest upheld dispute counts, so /vendor/join gives
--         the audited refusal, not an older dispute's "pending retry".
-- P-R3. A removed user who belongs to a vendor company again kept every tenant organization of the disputed company. The
--       application now skips only the organizations of the tenants their current company works with, and removes the
--       rest: vendor.user_company_tenants names the user's company and the tenants it has a relationship with (any
--       status), for the platform console only.
--
-- No new table, so no new row-level security policy; cr_disputes, vendor_users and relationships keep their forced
-- policies. Every function keeps its caller check (ADR-0012 point 4), runs as its owner with search_path pinned to
-- vendor, pg_temp, and is executable by erp_app only.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create or replace function vendor.dispute_claimant_is_admin(p_dispute_id uuid)
    returns boolean
    language plpgsql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    perform vendor.require_platform_admin_session();
    return exists (
        select 1
        from vendor.cr_disputes d
        join vendor.vendor_users u on u.company_id = d.company_id and u.user_id = d.claimant_user_id and u.role = 'vendor-admin'
        where d.id = p_dispute_id
          and d.status = 'upheld'
          and not exists (
              select 1 from vendor.cr_disputes l
              where l.company_id = d.company_id and l.status = 'upheld' and l.resolved_at > d.resolved_at));
end
$$;

create or replace function vendor.supersede_dispute_idp(p_dispute_id uuid, p_details jsonb)
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
    set idp_outcome = 'superseded', idp_recorded_at = now(), idp_details = p_details
    where d.id = p_dispute_id
      and d.status = 'upheld'
      and (d.idp_outcome is null or d.idp_outcome = 'failed')
      and (not exists (
               select 1 from vendor.vendor_users u
               where u.company_id = d.company_id and u.user_id = d.claimant_user_id and u.role = 'vendor-admin')
           or exists (
               select 1 from vendor.cr_disputes l
               where l.company_id = d.company_id and l.status = 'upheld' and l.resolved_at > d.resolved_at));
    return found;
end
$$;

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
          and not exists (
              select 1 from vendor.cr_disputes l
              where l.company_id = d.company_id and l.status = 'upheld' and l.resolved_at > d.resolved_at)
          and (d.idp_details is null
               or d.idp_details ->> ('organization:add:' || p_organization_alias) = 'failed'
               or d.idp_details ->> 'organization:lookup' = 'failed'))
$$;

-- P-R3: the user's vendor company and the tenants it works with; no row when the user has no vendor company, one row
-- with a null tenant when it works with none. Platform console only.
create function vendor.user_company_tenants(p_user_id text)
    returns table (company_id uuid, tenant_id uuid)
    language plpgsql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    perform vendor.require_platform_admin_session();
    return query
        select u.company_id, r.tenant_id
        from vendor.vendor_users u
        left join vendor.relationships r on r.company_id = u.company_id
        where u.user_id = p_user_id
        order by r.tenant_id;
end
$$;

revoke all on function vendor.dispute_claimant_is_admin(uuid) from public;
revoke all on function vendor.supersede_dispute_idp(uuid, jsonb) from public;
revoke all on function vendor.claimant_awaiting_organization(text) from public;
revoke all on function vendor.user_company_tenants(text) from public;
grant execute on function vendor.dispute_claimant_is_admin(uuid) to erp_app;
grant execute on function vendor.supersede_dispute_idp(uuid, jsonb) to erp_app;
grant execute on function vendor.claimant_awaiting_organization(text) to erp_app;
grant execute on function vendor.user_company_tenants(text) to erp_app;
