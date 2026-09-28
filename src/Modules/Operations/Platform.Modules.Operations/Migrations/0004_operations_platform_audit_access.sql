-- Pentest P-7 (ADR-0012 point 4): ops.platform_audit had no row-level security and erp_app held SELECT and INSERT, so
-- every app session, a vendor's on a tenant host included, read every company's consent history there and could write
-- forged entries. Since this migration:
-- 1. Writes go only through ops.write_platform_audit, a security-definer function that sets the time (now()) and, for a
--    session with a tenant or vendor context, requires the entry's actor to be the session's acting user. erp_app loses
--    INSERT on the table.
-- 2. Reads stay a plain SELECT but under forced row-level security: only a session with neither a tenant nor a vendor
--    context (the platform console and the worker) sees rows; any other session sees none. Until a separate worker role
--    exists (ADR-0012), "no context" also matches every platform-host session, which serves only platform admins.
-- The migration owner (BYPASSRLS, see the vendors migrations) keeps writing directly (vendor.unpark_document).

revoke insert on ops.platform_audit from erp_app;

alter table ops.platform_audit enable row level security;
alter table ops.platform_audit force row level security;
drop policy if exists platform_audit_read on ops.platform_audit;
create policy platform_audit_read on ops.platform_audit
    for select
    using (platform.current_tenant() is null and platform.current_vendor_company() is null);

create function ops.write_platform_audit(
    p_id uuid, p_actor_id text, p_action text, p_subject_type text, p_subject_id text, p_data jsonb)
    returns void
    language plpgsql
    volatile
    security definer
    set search_path = ops, pg_temp
as $$
begin
    if (platform.current_tenant() is not null or platform.current_vendor_company() is not null)
       and p_actor_id is distinct from platform.current_user_id() then
        raise exception 'A tenant or vendor session writes platform audit entries only as its own acting user.'
            using errcode = 'insufficient_privilege';
    end if;

    insert into ops.platform_audit (id, occurred_at, actor_id, action, subject_type, subject_id, data)
    values (p_id, now(), p_actor_id, p_action, p_subject_type, p_subject_id, coalesce(p_data, '{}'::jsonb));
end
$$;

revoke all on function ops.write_platform_audit(uuid, text, text, text, text, jsonb) from public;
grant execute on function ops.write_platform_audit(uuid, text, text, text, text, jsonb) to erp_app;

comment on table ops.platform_audit is
    'Platform audit (Operations module), append-only. erp_app writes only through ops.write_platform_audit and reads only without a tenant or vendor context (operations migration 0004). vendor.unpark_document (vendors migrations 0007 and 0008) inserts vendor.document_unparked directly as the migration owner with a version 4 id.';
