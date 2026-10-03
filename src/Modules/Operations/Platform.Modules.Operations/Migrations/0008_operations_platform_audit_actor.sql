-- W-41 (F-41, review of W-36 2026-10-03): ops.write_platform_audit (0004) required the entry's actor to be the session's
-- acting user only for a session with a tenant or vendor context, so a platform console session (erp_app, no context) could
-- write an entry under any actor id, or none. Since this migration:
-- - an erp_app session, whatever its context, writes only as platform.current_user_id() and must have one;
-- - a free actor (the system, null, or any other id) is for the worker only: a role that holds erp_worker's privileges (usage of the role; session_user, because
--   current_user is the owner inside a security-definer function) whose session has no acting user. The worker never has a
--   user; its jobs run with no context (health, usage) or with a vendor context (document rescan, null actor), both kept.
-- Callers checked: the console and vendor pages write as the signed-in user; VendorDocuments (parked and infected from the
-- rescan job) and the vendor and tenant sessions' own entries are covered above. The migration owner (the superuser or
-- BYPASSRLS role) passes the check only when it is a superuser or holds erp_worker's privileges. Grants are unchanged.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the operations migration as a role with BYPASSRLS (or the superuser): its security-definer functions write across row-level security as their owner.';
    end if;

    if not exists (select 1 from pg_roles where rolname = 'erp_worker') then
        raise exception 'Run the platform migrations first: platform 0008 creates erp_worker.';
    end if;
end
$$;

create or replace function ops.write_platform_audit(
    p_id uuid, p_actor_id text, p_action text, p_subject_type text, p_subject_id text, p_data jsonb)
    returns void
    language plpgsql
    volatile
    security definer
    set search_path = ops, pg_temp
as $$
begin
    if not (pg_has_role(session_user, 'erp_worker', 'usage') and platform.current_user_id() is null)
       and (platform.current_user_id() is null or p_actor_id is distinct from platform.current_user_id()) then
        raise exception 'Platform audit entries are written only as the session''s own acting user; a free actor is the worker''s.'
            using errcode = 'insufficient_privilege';
    end if;

    insert into ops.platform_audit (id, occurred_at, actor_id, action, subject_type, subject_id, data)
    values (p_id, now(), p_actor_id, p_action, p_subject_type, p_subject_id, coalesce(p_data, '{}'::jsonb));
end
$$;

revoke all on function ops.write_platform_audit(uuid, text, text, text, text, jsonb) from public;
grant execute on function ops.write_platform_audit(uuid, text, text, text, text, jsonb) to erp_app;
