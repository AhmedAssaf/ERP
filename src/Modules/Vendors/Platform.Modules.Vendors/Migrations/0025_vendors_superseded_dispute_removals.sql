-- W-33 second review of PR #5 (2026-09-30):
--
-- B-1. vendor.supersede_dispute_idp (0024) checked and marked in one statement, and the application then ran nothing, so
--      an earlier uphold's failed removal steps were dropped: a later uphold removes only the company's vendor users of
--      its own time (vendor.resolve_cr_dispute, 0019), and the earlier uphold's removed users (the squatter) had no row by
--      then, so their realm role and tenant organization memberships stayed with no trace (ADR-0013 point 5: the removed
--      users lose both). The check and the mark are now two calls:
--      - vendor.dispute_claimant_is_admin answers whether the dispute's claimant is still the company's vendor admin;
--        the application asks it before a retry (superseded or not) and again after its steps (m-1: a later uphold that
--        committed while the steps ran makes the application take back what the run granted).
--      - vendor.supersede_dispute_idp(uuid, jsonb) marks the dispute superseded with the outcome of every step, only
--        while its claimant is still not the company's vendor admin (the company row locked FOR SHARE, 0019 L-1). The
--        application calls it only when no step failed; otherwise it records the outcome as failed (0019's
--        vendor.record_dispute_idp_outcome) and the dispute stays listed for another retry.
--
-- No new table, so no new row-level security policy; cr_disputes keeps its forced policy of 0019. Both functions keep the
-- caller check (ADR-0012 point 4: a platform console session, no tenant, no vendor context, an acting user) and are
-- executable by erp_app only.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

drop function vendor.supersede_dispute_idp(uuid);

-- True when the upheld dispute's claimant is still its company's vendor admin; false otherwise (also for a dispute that
-- does not exist or is not upheld).
create function vendor.dispute_claimant_is_admin(p_dispute_id uuid)
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
        where d.id = p_dispute_id and d.status = 'upheld');
end
$$;

-- Marks an upheld dispute that needed a retry as superseded, with the outcome of each step, while its claimant is not the
-- company's vendor admin. True when marked; false, and nothing changed, otherwise.
create function vendor.supersede_dispute_idp(p_dispute_id uuid, p_details jsonb)
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
      and not exists (
          select 1 from vendor.vendor_users u
          where u.company_id = d.company_id and u.user_id = d.claimant_user_id and u.role = 'vendor-admin');
    return found;
end
$$;

revoke all on function vendor.dispute_claimant_is_admin(uuid) from public;
revoke all on function vendor.supersede_dispute_idp(uuid, jsonb) from public;
grant execute on function vendor.dispute_claimant_is_admin(uuid) to erp_app;
grant execute on function vendor.supersede_dispute_idp(uuid, jsonb) to erp_app;
