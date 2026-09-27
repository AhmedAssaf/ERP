-- Second pentest of the vendor slice (ADR-0012 point 4, ADR-0010 point 2).
-- P-14: vendor.approve_relationship (0014) refused a vendor context and a vendor user, but any other acting user passed
--       (an applicant, staff of another tenant). It now requires an active identity.members row of the tenant holding
--       contracts-officer or tenant-admin (read as the function's owner).
-- P-11: vendor.consent_grant_in_force took the date from its caller, so an export could rely on a grant that starts later
--       or has ended. The date parameter goes; the check is always about today in Riyadh (UTC+3 all year).
-- P-10: with a tenant and no vendor context it answered about any company. A tenant session now needs a relationship
--       with the company; a session without a context (an export) and the company's own vendor context still may ask.
-- P-12: a ledger row could claim an earlier recording time, and its actor could be a user of another company. One trigger
--       now holds every row rule: occurred_at is the database's now() (a row naming another time is refused), the actor is
--       a user of the row's company, and a grant starts today in Riyadh or later (0015's rule, now in the same trigger).

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create or replace function vendor.approve_relationship(p_company_id uuid)
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_tenant uuid := platform.current_tenant();
    v_user   text := platform.current_user_id();
begin
    if v_tenant is null or v_user is null then
        raise exception 'A vendor is approved by a signed-in user of the tenant.' using errcode = 'insufficient_privilege';
    end if;

    if platform.current_vendor_company() is not null
       or exists (select 1 from vendor.vendor_users u where u.user_id = v_user) then
        raise exception 'A vendor is approved by tenant staff, never in a vendor''s session.' using errcode = 'insufficient_privilege';
    end if;

    if not exists (select 1 from identity.members m
                   where m.tenant_id = v_tenant
                     and m.user_id = v_user
                     and m.status = 'active'
                     and m.roles && array['contracts-officer', 'tenant-admin']) then
        raise exception 'A vendor is approved by an active contracts officer or tenant admin of the tenant.' using errcode = 'insufficient_privilege';
    end if;

    update vendor.relationships
    set status = 'approved', approved_by = v_user
    where tenant_id = v_tenant and company_id = p_company_id and status = 'pending';
    return found;
end
$$;

revoke all on function vendor.approve_relationship(uuid) from public;
grant execute on function vendor.approve_relationship(uuid) to erp_app;

drop function vendor.consent_grant_in_force(uuid, uuid, text, date);

create function vendor.consent_grant_in_force(p_company_id uuid, p_recipient_id uuid, p_scope text)
    returns uuid
    language plpgsql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_vendor uuid := platform.current_vendor_company();
    v_tenant uuid := platform.current_tenant();
    v_today  date := ((now() + interval '3 hours') at time zone 'UTC')::date;
begin
    if v_vendor is not null and v_vendor <> p_company_id then
        raise exception 'A vendor checks the consent of its own company only.' using errcode = 'insufficient_privilege';
    end if;

    if v_vendor is null and v_tenant is not null
       and not exists (select 1 from vendor.relationships r where r.tenant_id = v_tenant and r.company_id = p_company_id) then
        raise exception 'Tenant staff check the consent of a company their tenant works with only.' using errcode = 'insufficient_privilege';
    end if;

    return (
        select g.id
        from vendor.consent_events g
        where g.company_id = p_company_id
          and g.recipient_id = p_recipient_id
          and g.scope = p_scope
          and g.kind = 'grant'
          and v_today between g.valid_from and g.valid_to
          and not exists (select 1 from vendor.consent_events r where r.revokes_grant_id = g.id)
        order by g.occurred_at desc, g.id desc
        limit 1);
end
$$;

revoke all on function vendor.consent_grant_in_force(uuid, uuid, text) from public;
grant execute on function vendor.consent_grant_in_force(uuid, uuid, text) to erp_app;

drop trigger tr_consent_events_no_backdating on vendor.consent_events;
drop function vendor.consent_grant_starts_today_or_later();

create function vendor.consent_event_rules()
    returns trigger
    language plpgsql
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    if new.occurred_at is distinct from now() then
        raise exception 'A consent row is recorded at the database''s time.' using errcode = 'check_violation';
    end if;

    if not exists (select 1 from vendor.vendor_users u where u.company_id = new.company_id and u.user_id = new.actor_id) then
        raise exception 'A consent row is recorded by a user of its own company.' using errcode = 'insufficient_privilege';
    end if;

    if new.kind = 'grant' and new.valid_from < ((now() + interval '3 hours') at time zone 'UTC')::date then
        raise exception 'A consent starts today (Riyadh) or later; it is never backdated.' using errcode = 'check_violation';
    end if;

    return new;
end
$$;

revoke all on function vendor.consent_event_rules() from public;

create trigger tr_consent_events_rules
    before insert on vendor.consent_events
    for each row execute function vendor.consent_event_rules();
