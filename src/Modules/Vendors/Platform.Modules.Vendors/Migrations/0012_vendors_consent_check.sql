-- The consent ledger's check (vendor plan task 6, F-64, V-12, ADR-0010 point 2). The ledger table vendor.consent_events
-- (0001, revocation key 0003) stays append-only: the application role may only select and insert, under the company
-- policy, so a grant or revocation is written only in the company's own vendor context and never by a tenant connection.
-- An export checks the ledger at the moment it runs, usually without that vendor context (a job, an operator), so the
-- check is a security-definer function answering one question: the grant in force for company, recipient and scope on a
-- date, or null. It refuses a caller in another company's vendor context, so a vendor never learns whom a competitor
-- consented to.
-- Recipients (V-13): none are seeded here or in any migration; the development seed adds a test recipient.
-- No new table, so no new row-level security policy.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

-- The grant of p_company to p_recipient for p_scope whose period holds p_on (first and last day included) and that no
-- revocation names; the newest when several do. Null when there is none.
create function vendor.consent_grant_in_force(p_company_id uuid, p_recipient_id uuid, p_scope text, p_on date)
    returns uuid
    language plpgsql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_vendor uuid := platform.current_vendor_company();
begin
    if v_vendor is not null and v_vendor <> p_company_id then
        raise exception 'A vendor checks the consent of its own company only.' using errcode = 'insufficient_privilege';
    end if;

    return (
        select g.id
        from vendor.consent_events g
        where g.company_id = p_company_id
          and g.recipient_id = p_recipient_id
          and g.scope = p_scope
          and g.kind = 'grant'
          and p_on between g.valid_from and g.valid_to
          and not exists (select 1 from vendor.consent_events r where r.revokes_grant_id = g.id)
        order by g.occurred_at desc, g.id desc
        limit 1);
end
$$;

revoke all on function vendor.consent_grant_in_force(uuid, uuid, text, date) from public;
grant execute on function vendor.consent_grant_in_force(uuid, uuid, text, date) to erp_app;
