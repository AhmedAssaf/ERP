-- Review of the vendor slice (2026-09-28): the consent ledger's row trigger (0016) raised check_violation, without a
-- name, both for a row naming a recording time other than now() and for a backdated grant, and ConsentLedger.GrantAsync
-- read every check violation as the vendor's invalid period. Each rule now raises under its own constraint name:
--   ck_consent_events_recorded_now   a row names a time other than the database's now() (a defect, never the vendor's)
--   ck_consent_events_not_backdated  a grant starts before today in Riyadh (the vendor's period; shown as such)
-- The actor rule keeps insufficient_privilege. Same trigger and function name; no table change.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create or replace function vendor.consent_event_rules()
    returns trigger
    language plpgsql
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    if new.occurred_at is distinct from now() then
        raise exception 'A consent row is recorded at the database''s time.'
            using errcode = 'check_violation', constraint = 'ck_consent_events_recorded_now', table = 'consent_events', schema = 'vendor';
    end if;

    if not exists (select 1 from vendor.vendor_users u where u.company_id = new.company_id and u.user_id = new.actor_id) then
        raise exception 'A consent row is recorded by a user of its own company.' using errcode = 'insufficient_privilege';
    end if;

    if new.kind = 'grant' and new.valid_from < ((now() + interval '3 hours') at time zone 'UTC')::date then
        raise exception 'A consent starts today (Riyadh) or later; it is never backdated.'
            using errcode = 'check_violation', constraint = 'ck_consent_events_not_backdated', table = 'consent_events', schema = 'vendor';
    end if;

    return new;
end
$$;

revoke all on function vendor.consent_event_rules() from public;
