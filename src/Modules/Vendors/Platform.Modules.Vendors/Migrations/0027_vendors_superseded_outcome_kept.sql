-- W-33 fourth review of PR #5 (2026-09-30), finding 1:
--
-- vendor.record_dispute_idp_outcome (0019) wrote the outcome of an upheld dispute whatever it held. A retry that started
-- while its claimant was still the company's vendor admin runs its steps; meanwhile a later uphold moves the company and a
-- parallel retry marks the dispute superseded (vendor.supersede_dispute_idp, 0026). When the slower retry then recorded
-- "updated" or "failed", it replaced "superseded": a failed outcome listed a dispute for a retry that would run as a
-- superseded one anyway, and an updated one hid that the company had moved on. The function now leaves a superseded
-- outcome as it is and answers false; the application reads the outcome again and treats the dispute as superseded.
--
-- No new table, so no new row-level security policy; cr_disputes keeps its forced policies. The function keeps its caller
-- check (ADR-0012 point 4), runs as its owner with search_path pinned to vendor, pg_temp, and is executable by erp_app only.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create or replace function vendor.record_dispute_idp_outcome(p_dispute_id uuid, p_updated boolean, p_details jsonb)
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    perform vendor.require_platform_admin_session();
    update vendor.cr_disputes d
    set idp_outcome = case when p_updated then 'updated' else 'failed' end, idp_recorded_at = now(), idp_details = p_details
    where d.id = p_dispute_id
      and d.status = 'upheld'
      and d.idp_outcome is distinct from 'superseded';
    return found;
end
$$;

-- The idp_outcome of an upheld dispute, for the platform console only: read again when a retry's own outcome was not
-- recorded or its supersede mark matched nothing, so the admin is told what the dispute holds now.
create function vendor.dispute_idp_outcome(p_dispute_id uuid)
    returns text
    language plpgsql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    perform vendor.require_platform_admin_session();
    return (select d.idp_outcome from vendor.cr_disputes d where d.id = p_dispute_id and d.status = 'upheld');
end
$$;

revoke all on function vendor.record_dispute_idp_outcome(uuid, boolean, jsonb) from public;
revoke all on function vendor.dispute_idp_outcome(uuid) from public;
grant execute on function vendor.record_dispute_idp_outcome(uuid, boolean, jsonb) to erp_app;
grant execute on function vendor.dispute_idp_outcome(uuid) to erp_app;
