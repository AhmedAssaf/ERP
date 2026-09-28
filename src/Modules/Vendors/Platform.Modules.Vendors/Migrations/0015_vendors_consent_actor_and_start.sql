-- Review of vendor plan task 6 (F-64, V-12): the database holds two consent rules on its own, not only the ledger service.
-- 1. The actor of a ledger row is the session's acting user: the company policy's with check on vendor.consent_events
--    also requires actor_id = platform.current_user_id(), so a vendor session cannot record a grant or revocation in
--    another person's name. Reads keep the company rule.
-- 2. No backdating: a grant starts today in Riyadh (UTC+3 all year) or later. A check constraint must not depend on the
--    clock (a restore would re-check old rows against a later date), so a trigger checks each new grant when it is
--    inserted. The table is append-only for the application role (0001), so there is no update to check.
-- No new table, so no new row-level security policy; the policy keeps its name vendor_isolation.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

drop policy vendor_isolation on vendor.consent_events;
create policy vendor_isolation on vendor.consent_events
    using (company_id = platform.current_vendor_company())
    with check (company_id = platform.current_vendor_company() and actor_id = platform.current_user_id());

create function vendor.consent_grant_starts_today_or_later()
    returns trigger
    language plpgsql
    set search_path = vendor, pg_temp
as $$
begin
    if new.kind = 'grant' and new.valid_from < ((now() + interval '3 hours') at time zone 'UTC')::date then
        raise exception 'A consent starts today (Riyadh) or later; it is never backdated.' using errcode = 'check_violation';
    end if;

    return new;
end
$$;

revoke all on function vendor.consent_grant_starts_today_or_later() from public;

create trigger tr_consent_events_no_backdating
    before insert on vendor.consent_events
    for each row execute function vendor.consent_grant_starts_today_or_later();
