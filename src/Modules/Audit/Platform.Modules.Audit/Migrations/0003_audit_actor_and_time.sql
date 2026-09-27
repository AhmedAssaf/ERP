-- Pentest P-8 and review 5 (ADR-0012 point 3): the tenant's audit log is written by vendors too, and staff read it as
-- fact, so a vendor session writes only as itself and never chooses the time.
-- 1. The insert policy also requires, with a vendor context, actor_id = platform.current_user_id().
-- 2. The database's clock: a trigger sets occurred_at = now() on every insert. A vendor session that names another time
--    is refused rather than corrected (the column's default is now() itself, so a writer that names no time passes).
--    IAuditWriter names no time since this migration.

drop policy if exists tenant_audit_insert on audit.events;
create policy tenant_audit_insert on audit.events
    for insert
    with check (
        tenant_id = platform.current_tenant()
        and (platform.current_vendor_company() is null or actor_id = platform.current_user_id()));

create function audit.events_database_time()
    returns trigger
    language plpgsql
    set search_path = audit, pg_temp
as $$
begin
    if platform.current_vendor_company() is not null and new.occurred_at is distinct from now() then
        raise exception 'A vendor session records audit events at the database''s time.' using errcode = 'insufficient_privilege';
    end if;

    new.occurred_at := now();
    return new;
end
$$;

revoke all on function audit.events_database_time() from public;

create trigger tr_events_database_time
    before insert on audit.events
    for each row execute function audit.events_database_time();
