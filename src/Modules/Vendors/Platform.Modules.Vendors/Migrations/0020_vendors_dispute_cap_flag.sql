-- W-33 second review (2026-09-29): the per-company cap of 0019 could be turned against the real owner. A squatter whose
-- company is verified could fill the company's five pending slots from throwaway accounts at no cost (open disputes hold
-- nothing, ADR-0013 decision 1), and the real owner's dispute was then refused. A dispute is now never refused for the
-- company's cap: it is recorded, and one raised while the company already has five or more pending disputes is marked
-- over_cap, so the platform console lists those grouped after the others with the company's pending count instead of
-- hiding them. The per-person limits stay: three pending disputes per claimant (0019) and the rate limit on every post of
-- /vendor/dispute (web host).
--
-- The worker's functions vendor.unalerted_cr_disputes and vendor.mark_cr_disputes_alerted (0019) answer only a session
-- with neither a tenant nor a vendor context nor an acting user. No database role marks the worker yet; until W-36 adds
-- one, "no context and no user" is the worker's guard, as for the other worker-only functions (ADR-0012 point 4).

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

alter table vendor.cr_disputes add column over_cap boolean not null default false;

create or replace function vendor.raise_cr_dispute(
    p_cr_number text, p_claimant_email text, p_claimant_name text, p_statement text,
    p_privacy_notice_version text, p_privacy_notice_culture text)
    returns uuid
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_tenant   uuid := platform.current_tenant();
    v_user     text := platform.current_user_id();
    v_company  uuid;
    v_id       uuid := gen_random_uuid();
    v_over_cap boolean;
begin
    if v_tenant is null or v_user is null or platform.current_vendor_company() is not null then
        raise exception 'A dispute is raised by a signed-in person on a tenant host, outside any vendor session.'
            using errcode = 'insufficient_privilege';
    end if;

    if exists (select 1 from vendor.vendor_users u where u.user_id = v_user) then
        raise exception 'This account already belongs to a vendor company.'
            using errcode = 'check_violation', constraint = 'ck_cr_disputes_claimant_not_vendor', table = 'cr_disputes', schema = 'vendor';
    end if;

    if exists (select 1 from identity.members m where m.user_id = v_user and m.status = 'active') then
        raise exception 'This account belongs to a staff member.'
            using errcode = 'check_violation', constraint = 'ck_cr_disputes_claimant_not_staff', table = 'cr_disputes', schema = 'vendor';
    end if;

    -- L-1: the company row first; an approval of the company in flight holds it FOR SHARE until it commits.
    select c.id into v_company from vendor.companies c where c.cr_number = p_cr_number for no key update;
    if v_company is null then
        return null;
    end if;

    -- L-2: one claimant's submissions one at a time, so the count below cannot be passed by concurrent ones.
    perform pg_advisory_xact_lock(hashtextextended('vendor.cr_disputes:' || v_user, 0));

    if (select count(*) from vendor.cr_disputes d where d.claimant_user_id = v_user and d.status in ('open', 'under_review')) >= 3 then
        raise exception 'A person has at most three pending disputes.'
            using errcode = 'check_violation', constraint = 'ck_cr_disputes_open_limit', table = 'cr_disputes', schema = 'vendor';
    end if;

    -- Never refused for the company's cap: flagged, so the console groups it without hiding it (the company row lock
    -- above makes the count exact).
    v_over_cap := (select count(*) from vendor.cr_disputes d where d.company_id = v_company and d.status in ('open', 'under_review')) >= 5;

    insert into vendor.cr_disputes (
        id, company_id, claimant_user_id, claimant_email, claimant_name, statement,
        privacy_notice_version, privacy_notice_culture, raised_on_tenant, over_cap)
    values (v_id, v_company, v_user, p_claimant_email, p_claimant_name, p_statement,
            p_privacy_notice_version, p_privacy_notice_culture, v_tenant, v_over_cap);
    return v_id;
end
$$;

-- The pending disputes with their flag and the company's pending count; disputes within the cap first, then the flagged
-- ones, each group oldest first.
drop function vendor.open_cr_disputes();

create function vendor.open_cr_disputes()
    returns table (
        id uuid, company_id uuid, cr_number text, name_ar text, name_en text,
        claimant_user_id text, claimant_email text, claimant_name text, statement text,
        raised_on_tenant uuid, raised_at timestamptz, registrant_user_id text, ownership_method text,
        status text, reviewed_at timestamptz, over_cap boolean, company_pending integer)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, d.company_id, c.cr_number, c.name_ar, c.name_en,
           d.claimant_user_id, d.claimant_email, d.claimant_name, d.statement,
           d.raised_on_tenant, d.raised_at, vendor.company_registrant(c.id), v.method,
           d.status, d.reviewed_at, d.over_cap,
           (select count(*)::integer from vendor.cr_disputes p where p.company_id = d.company_id and p.status in ('open', 'under_review'))
    from vendor.cr_disputes d
    join vendor.companies c on c.id = d.company_id
    left join vendor.ownership_verifications v on v.company_id = d.company_id
    where d.status in ('open', 'under_review')
      and platform.current_tenant() is null
      and platform.current_vendor_company() is null
      and platform.current_user_id() is not null
    order by d.over_cap, d.raised_at, d.id
$$;

revoke all on function vendor.raise_cr_dispute(text, text, text, text, text, text) from public;
revoke all on function vendor.open_cr_disputes() from public;
grant execute on function vendor.raise_cr_dispute(text, text, text, text, text, text) to erp_app;
grant execute on function vendor.open_cr_disputes() to erp_app;
