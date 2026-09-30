-- W-33 review and pentest (2026-09-29) of migration 0018, with the user's decision of the same day:
--
-- Decision 1: a dispute holds approvals only after triage. A newly raised dispute (open) holds nothing for a company whose
--   ownership is verified; it holds that company's approvals once a platform admin accepts it for review (under_review,
--   vendor.accept_cr_dispute, audited by the application). A company that is not verified stays unapprovable as before,
--   and its first verification waits while any claim on it is pending (open or under review): an officer never confirms the
--   registrant while someone else claims the company. At most five pending disputes per company, three per claimant.
-- L-1: approve_relationship and verify_ownership lock the company row FOR SHARE, raise_cr_dispute, accept_cr_dispute and
--   resolve_cr_dispute FOR NO KEY UPDATE, after the caller check and before any check of state, so an approval and a
--   dispute on the same company are serialised: either the dispute waits for the approval, or the approval sees it.
-- L-2: raise_cr_dispute takes a transaction advisory lock per claimant before counting their pending disputes.
-- L-3: a platform admin never accepts or resolves a dispute they raised themselves (function and table constraint).
-- L-4: the select policy on cr_disputes also needs an acting user. The app role keeps SELECT: no application code reads
--   the table directly (only the security-definer functions do), but a session without an acting user must see no rows
--   rather than an error, as the pentest's regression test states.
-- L-5: erp_app loses INSERT on vendor.vendor_users and UPDATE on vendor.companies; no application path used them (every
--   vendor user is written by register_company or resolve_cr_dispute, no screen edits a company yet). A company has at
--   most one vendor admin (unique index), so competing upholds can never leave two.
-- Reviewer: an uphold keeps the verification it replaces on the dispute (superseded_verification); the identity
--   provider's outcome after an uphold is stored on the dispute (idp_outcome, and idp_details: one entry per step, among
--   them the claimant added to and the removed users taken out of each related tenant's organization, since W-21's
--   /vendor/join no longer restores a membership) so the console can show it and retry; the worker's alert job marks each
--   new dispute once it told the platform admins (alerted_at).

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

-- Disputes: the review state and the new columns -----------------------------------------------------------------------

alter table vendor.cr_disputes drop constraint ck_cr_disputes_status;
alter table vendor.cr_disputes drop constraint ck_cr_disputes_resolution;
alter table vendor.cr_disputes
    add column reviewed_by             text        null,
    add column reviewed_at             timestamptz null,
    add column superseded_verification jsonb       null,
    add column idp_outcome             text        null,
    add column idp_recorded_at         timestamptz null,
    add column idp_details             jsonb       null,
    add column alerted_at              timestamptz null;
alter table vendor.cr_disputes
    add constraint ck_cr_disputes_status check (status in ('open', 'under_review', 'upheld', 'rejected')),
    add constraint ck_cr_disputes_resolution check (
        (status in ('open', 'under_review') and resolved_by is null and resolved_at is null and resolution_note is null
            and removed_user_ids is null and idp_outcome is null)
        or (status = 'rejected' and resolved_by is not null and resolved_at is not null and resolution_note is not null
            and removed_user_ids is null and idp_outcome is null)
        or (status = 'upheld' and resolved_by is not null and resolved_at is not null and resolution_note is not null
            and removed_user_ids is not null)),
    add constraint ck_cr_disputes_review check (
        (reviewed_by is null) = (reviewed_at is null) and (status <> 'under_review' or reviewed_by is not null)),
    add constraint ck_cr_disputes_idp_outcome check (
        (idp_outcome is null or idp_outcome in ('updated', 'failed')) and (idp_outcome is null) = (idp_recorded_at is null)),
    -- L-3: nobody decides about their own claim.
    add constraint ck_cr_disputes_not_self check (
        resolved_by is distinct from claimant_user_id and reviewed_by is distinct from claimant_user_id);

drop index vendor.ux_cr_disputes_open;
create unique index ux_cr_disputes_open on vendor.cr_disputes (company_id, claimant_user_id) where status in ('open', 'under_review');
create index ix_cr_disputes_company_pending on vendor.cr_disputes (company_id) where status in ('open', 'under_review');
create index ix_cr_disputes_unalerted on vendor.cr_disputes (raised_at) where alerted_at is null;

-- L-4
drop policy cr_dispute_access on vendor.cr_disputes;
create policy cr_dispute_access on vendor.cr_disputes
    for select
    using (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is not null);

-- L-5
revoke insert on vendor.vendor_users from erp_app;
revoke update on vendor.companies from erp_app;
create unique index ux_vendor_users_one_admin on vendor.vendor_users (company_id) where role = 'vendor-admin';

-- Approval and verification ------------------------------------------------------------------------------------------

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
    perform vendor.require_vendor_manager();

    -- L-1: before any check of state, so a dispute on the company waits for this approval or this approval sees it.
    perform 1 from vendor.companies c where c.id = p_company_id for share;

    if exists (select 1 from vendor.relationships r
               where r.tenant_id = v_tenant and r.company_id = p_company_id and r.status = 'pending') then
        if not exists (select 1 from vendor.ownership_verifications v where v.company_id = p_company_id) then
            raise exception 'The company''s ownership is verified before its first approval.'
                using errcode = 'check_violation', constraint = 'ck_relationships_ownership_verified', table = 'relationships', schema = 'vendor';
        end if;

        -- Decision 1: only a dispute a platform admin accepted for review holds a verified company.
        if exists (select 1 from vendor.cr_disputes d where d.company_id = p_company_id and d.status = 'under_review') then
            raise exception 'WaslaBid is reviewing who owns this company.'
                using errcode = 'check_violation', constraint = 'ck_relationships_not_disputed', table = 'relationships', schema = 'vendor';
        end if;
    end if;

    update vendor.relationships
    set status = 'approved', approved_by = v_user
    where tenant_id = v_tenant and company_id = p_company_id and status = 'pending';
    return found;
end
$$;

create or replace function vendor.verify_ownership(p_company_id uuid, p_method text, p_note text)
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_tenant     uuid := platform.current_tenant();
    v_registrant text;
begin
    perform vendor.require_vendor_manager();

    if not exists (select 1 from vendor.relationships r where r.tenant_id = v_tenant and r.company_id = p_company_id) then
        raise exception 'Ownership is verified by staff of a tenant that works with the company.' using errcode = 'insufficient_privilege';
    end if;

    -- L-1: before any check of state.
    perform 1 from vendor.companies c where c.id = p_company_id for share;

    if exists (select 1 from vendor.ownership_verifications v where v.company_id = p_company_id) then
        return false;
    end if;

    if p_method is distinct from 'manual' and p_method is distinct from 'wathq' then
        raise exception 'An officer verifies ownership manually or with Wathq.'
            using errcode = 'check_violation', constraint = 'ck_ownership_verifications_method', table = 'ownership_verifications', schema = 'vendor';
    end if;

    if p_method = 'wathq' and (select s.method from vendor.ownership_settings s where s.id) <> 'wathq' then
        raise exception 'A Wathq check is recorded only while the platform uses Wathq.'
            using errcode = 'check_violation', constraint = 'ck_ownership_method_setting', table = 'ownership_verifications', schema = 'vendor';
    end if;

    -- A first verification waits while anyone claims the company, triaged or not.
    if exists (select 1 from vendor.cr_disputes d where d.company_id = p_company_id and d.status in ('open', 'under_review')) then
        raise exception 'The company''s ownership is under review by WaslaBid.'
            using errcode = 'check_violation', constraint = 'ck_ownership_not_disputed', table = 'ownership_verifications', schema = 'vendor';
    end if;

    if not exists (select 1 from vendor.documents d
                   where d.company_id = p_company_id and d.type = 'cr_certificate' and d.is_current and d.scan_status = 'clean') then
        raise exception 'Ownership is checked against the company''s current CR certificate, and it has none.'
            using errcode = 'check_violation', constraint = 'ck_ownership_certificate', table = 'ownership_verifications', schema = 'vendor';
    end if;

    v_registrant := vendor.company_registrant(p_company_id);
    if v_registrant is null then
        raise exception 'The company has no vendor admin whose ownership could be verified.'
            using errcode = 'check_violation', constraint = 'ck_ownership_certificate', table = 'ownership_verifications', schema = 'vendor';
    end if;

    insert into vendor.ownership_verifications (company_id, method, registrant_user_id, verified_by, verified_in_tenant, note)
    values (p_company_id, p_method, v_registrant, platform.current_user_id(), v_tenant, p_note)
    on conflict (company_id) do nothing;
    return found;
end
$$;

-- What staff know: "disputed" is now "held", as the two functions above apply it.
create or replace function vendor.related_ownership(p_company_id uuid)
    returns table (
        registrant_user_id text, verified boolean, method text, verified_here boolean, disputed boolean, has_certificate boolean)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select vendor.company_registrant(c.id),
           v.company_id is not null,
           v.method,
           coalesce(v.verified_in_tenant = platform.current_tenant(), false),
           exists (select 1 from vendor.cr_disputes d
                   where d.company_id = c.id
                     and (d.status = 'under_review' or (d.status = 'open' and v.company_id is null))),
           exists (select 1 from vendor.documents d
                   where d.company_id = c.id and d.type = 'cr_certificate' and d.is_current and d.scan_status = 'clean')
    from vendor.companies c
    left join vendor.ownership_verifications v on v.company_id = c.id
    where c.id = p_company_id
      and platform.current_vendor_company() is null
      and exists (select 1 from vendor.relationships r
                  where r.tenant_id = platform.current_tenant() and r.company_id = c.id)
$$;

-- Disputes -------------------------------------------------------------------------------------------------------------

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
    v_tenant  uuid := platform.current_tenant();
    v_user    text := platform.current_user_id();
    v_company uuid;
    v_id      uuid := gen_random_uuid();
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

    if (select count(*) from vendor.cr_disputes d where d.company_id = v_company and d.status in ('open', 'under_review')) >= 5 then
        raise exception 'A company has at most five pending disputes.'
            using errcode = 'check_violation', constraint = 'ck_cr_disputes_company_limit', table = 'cr_disputes', schema = 'vendor';
    end if;

    insert into vendor.cr_disputes (
        id, company_id, claimant_user_id, claimant_email, claimant_name, statement,
        privacy_notice_version, privacy_notice_culture, raised_on_tenant)
    values (v_id, v_company, v_user, p_claimant_email, p_claimant_name, p_statement,
            p_privacy_notice_version, p_privacy_notice_culture, v_tenant);
    return v_id;
end
$$;

-- The platform console's session: no tenant, no vendor context, an acting user.
create function vendor.require_platform_admin_session()
    returns text
    language plpgsql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_user text := platform.current_user_id();
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null or v_user is null then
        raise exception 'A dispute is handled by a platform admin in the platform console.' using errcode = 'insufficient_privilege';
    end if;

    return v_user;
end
$$;

-- Decision 1: a platform admin accepts an open dispute for review; from then on it holds the company's approvals.
-- Returns the company, or null when the dispute does not exist or is not open.
create function vendor.accept_cr_dispute(p_dispute_id uuid)
    returns uuid
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_user     text := vendor.require_platform_admin_session();
    v_company  uuid;
    v_claimant text;
begin
    select d.company_id, d.claimant_user_id into v_company, v_claimant from vendor.cr_disputes d where d.id = p_dispute_id;
    if not found then
        return null;
    end if;

    if v_claimant = v_user then
        raise exception 'A platform admin never handles a dispute they raised.' using errcode = 'insufficient_privilege';
    end if;

    -- L-1: serialised with approvals and other decisions on the company.
    perform 1 from vendor.companies c where c.id = v_company for no key update;

    update vendor.cr_disputes d
    set status = 'under_review', reviewed_by = v_user, reviewed_at = now()
    where d.id = p_dispute_id and d.status = 'open';
    return case when found then v_company end;
end
$$;

-- The pending disputes, oldest first, with their state; for the platform console.
drop function vendor.open_cr_disputes();

create function vendor.open_cr_disputes()
    returns table (
        id uuid, company_id uuid, cr_number text, name_ar text, name_en text,
        claimant_user_id text, claimant_email text, claimant_name text, statement text,
        raised_on_tenant uuid, raised_at timestamptz, registrant_user_id text, ownership_method text,
        status text, reviewed_at timestamptz)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, d.company_id, c.cr_number, c.name_ar, c.name_en,
           d.claimant_user_id, d.claimant_email, d.claimant_name, d.statement,
           d.raised_on_tenant, d.raised_at, vendor.company_registrant(c.id), v.method,
           d.status, d.reviewed_at
    from vendor.cr_disputes d
    join vendor.companies c on c.id = d.company_id
    left join vendor.ownership_verifications v on v.company_id = d.company_id
    where d.status in ('open', 'under_review')
      and platform.current_tenant() is null
      and platform.current_vendor_company() is null
      and platform.current_user_id() is not null
    order by d.raised_at, d.id
$$;

-- Closes an open or accepted dispute. Upheld: the company's vendor users are removed, the claimant becomes its vendor
-- admin with the privacy notice accepted when raising it, its ownership is recorded as verified (method dispute; the
-- verification it replaces is kept on the dispute), and the company's other pending disputes close as rejected, so
-- competing upholds never leave two admins. Rejected: only the dispute changes. No row when the dispute does not exist
-- or is no longer pending.
drop function vendor.resolve_cr_dispute(uuid, boolean, text);

create function vendor.resolve_cr_dispute(p_dispute_id uuid, p_uphold boolean, p_note text)
    returns table (company_id uuid, claimant_user_id text, removed_user_ids text[], closed_other_disputes integer)
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_user       text := vendor.require_platform_admin_session();
    v_company    uuid;
    v_claimant   text;
    v_dispute    vendor.cr_disputes%rowtype;
    v_removed    text[];
    v_superseded jsonb;
    v_closed     integer := 0;
begin
    select d.company_id, d.claimant_user_id into v_company, v_claimant from vendor.cr_disputes d where d.id = p_dispute_id;
    if not found then
        return;
    end if;

    -- L-3
    if v_claimant = v_user then
        raise exception 'A platform admin never resolves a dispute they raised.' using errcode = 'insufficient_privilege';
    end if;

    -- L-1: the company row first; a competing decision on the company waits here and then finds its dispute closed.
    perform 1 from vendor.companies c where c.id = v_company for no key update;

    select * into v_dispute from vendor.cr_disputes d
    where d.id = p_dispute_id and d.status in ('open', 'under_review')
    for update;
    if not found then
        return;
    end if;

    if not p_uphold then
        update vendor.cr_disputes d
        set status = 'rejected', resolved_by = v_user, resolved_at = now(), resolution_note = p_note
        where d.id = p_dispute_id;
        return query select v_dispute.company_id, v_dispute.claimant_user_id, null::text[], 0;
        return;
    end if;

    if exists (select 1 from vendor.vendor_users u where u.user_id = v_dispute.claimant_user_id) then
        raise exception 'The claimant meanwhile belongs to a vendor company.'
            using errcode = 'check_violation', constraint = 'ck_cr_disputes_claimant_not_vendor', table = 'cr_disputes', schema = 'vendor';
    end if;

    if exists (select 1 from identity.members m where m.user_id = v_dispute.claimant_user_id and m.status = 'active') then
        raise exception 'The claimant meanwhile is a staff member of a tenant.'
            using errcode = 'check_violation', constraint = 'ck_cr_disputes_claimant_not_staff', table = 'cr_disputes', schema = 'vendor';
    end if;

    select coalesce(array_agg(u.user_id order by u.created_at, u.id), array[]::text[]) into v_removed
    from vendor.vendor_users u where u.company_id = v_dispute.company_id;
    delete from vendor.vendor_users u where u.company_id = v_dispute.company_id;

    insert into vendor.vendor_users (
        id, company_id, user_id, role, privacy_notice_version, privacy_notice_culture, privacy_accepted_at)
    values (gen_random_uuid(), v_dispute.company_id, v_dispute.claimant_user_id, 'vendor-admin',
            v_dispute.privacy_notice_version, v_dispute.privacy_notice_culture, v_dispute.raised_at);

    select to_jsonb(v) into v_superseded from vendor.ownership_verifications v where v.company_id = v_dispute.company_id;

    insert into vendor.ownership_verifications (company_id, method, registrant_user_id, verified_by, verified_in_tenant, note)
    values (v_dispute.company_id, 'dispute', v_dispute.claimant_user_id, v_user, null, p_note)
    on conflict on constraint ownership_verifications_pkey do update
        set method = 'dispute', registrant_user_id = excluded.registrant_user_id, verified_by = excluded.verified_by,
            verified_at = now(), verified_in_tenant = null, note = excluded.note;

    update vendor.cr_disputes d
    set status = 'upheld', resolved_by = v_user, resolved_at = now(), resolution_note = p_note, removed_user_ids = v_removed,
        superseded_verification = v_superseded
    where d.id = p_dispute_id;

    update vendor.cr_disputes d
    set status = 'rejected', resolved_by = v_user, resolved_at = now(),
        resolution_note = 'Closed without a decision of its own: another dispute for this company was upheld.'
    where d.company_id = v_dispute.company_id and d.id <> p_dispute_id and d.status in ('open', 'under_review');
    get diagnostics v_closed = row_count;

    return query select v_dispute.company_id, v_dispute.claimant_user_id, v_removed, v_closed;
end
$$;

-- Reviewer, major 2: what the identity provider did after an uphold (overall, and step by step), so the console shows a
-- failure until it is retried.
create function vendor.record_dispute_idp_outcome(p_dispute_id uuid, p_updated boolean, p_details jsonb)
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
    where d.id = p_dispute_id and d.status = 'upheld';
    return found;
end
$$;

-- The tenants the company of an upheld dispute works with: their organizations get the claimant and lose the removed
-- users when the identity provider is updated (W-21 closed membership restores through /vendor/join).
create function vendor.dispute_related_tenants(p_dispute_id uuid)
    returns table (tenant_id uuid)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select r.tenant_id
    from vendor.cr_disputes d
    join vendor.relationships r on r.company_id = d.company_id
    where d.id = p_dispute_id
      and d.status = 'upheld'
      and platform.current_tenant() is null
      and platform.current_vendor_company() is null
      and platform.current_user_id() is not null
    order by r.tenant_id
$$;

-- Upheld disputes whose identity provider update failed or was never recorded, oldest first.
create function vendor.upheld_disputes_needing_idp()
    returns table (
        id uuid, company_id uuid, cr_number text, name_ar text, name_en text, claimant_user_id text, claimant_name text,
        removed_user_ids text[], resolved_at timestamptz, idp_outcome text, idp_details jsonb)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, d.company_id, c.cr_number, c.name_ar, c.name_en, d.claimant_user_id, d.claimant_name,
           d.removed_user_ids, d.resolved_at, d.idp_outcome, d.idp_details
    from vendor.cr_disputes d
    join vendor.companies c on c.id = d.company_id
    where d.status = 'upheld'
      and d.idp_outcome is distinct from 'updated'
      and platform.current_tenant() is null
      and platform.current_vendor_company() is null
      and platform.current_user_id() is not null
    order by d.resolved_at, d.id
$$;

-- The worker's alert job: disputes nobody told the platform admins about yet (ids only), and marking them told. Only a
-- session with neither a tenant nor a vendor context nor an acting user (the worker) may call them.
create function vendor.unalerted_cr_disputes(p_limit integer)
    returns table (id uuid)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id from vendor.cr_disputes d
    where d.alerted_at is null
      and platform.current_tenant() is null
      and platform.current_vendor_company() is null
      and platform.current_user_id() is null
    order by d.raised_at, d.id
    limit greatest(least(p_limit, 1000), 0)
$$;

create function vendor.mark_cr_disputes_alerted(p_ids uuid[])
    returns integer
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_count integer;
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null or platform.current_user_id() is not null then
        raise exception 'Only the worker marks disputes as announced.' using errcode = 'insufficient_privilege';
    end if;

    update vendor.cr_disputes d set alerted_at = now() where d.id = any(p_ids) and d.alerted_at is null;
    get diagnostics v_count = row_count;
    return v_count;
end
$$;

-- resolve_cr_dispute of 0018 checked the session itself; set_ownership_method keeps its own check.
revoke all on function vendor.require_platform_admin_session() from public;
revoke all on function vendor.approve_relationship(uuid) from public;
revoke all on function vendor.verify_ownership(uuid, text, text) from public;
revoke all on function vendor.related_ownership(uuid) from public;
revoke all on function vendor.raise_cr_dispute(text, text, text, text, text, text) from public;
revoke all on function vendor.accept_cr_dispute(uuid) from public;
revoke all on function vendor.open_cr_disputes() from public;
revoke all on function vendor.resolve_cr_dispute(uuid, boolean, text) from public;
revoke all on function vendor.record_dispute_idp_outcome(uuid, boolean, jsonb) from public;
revoke all on function vendor.dispute_related_tenants(uuid) from public;
revoke all on function vendor.upheld_disputes_needing_idp() from public;
revoke all on function vendor.unalerted_cr_disputes(integer) from public;
revoke all on function vendor.mark_cr_disputes_alerted(uuid[]) from public;
grant execute on function vendor.approve_relationship(uuid) to erp_app;
grant execute on function vendor.verify_ownership(uuid, text, text) to erp_app;
grant execute on function vendor.related_ownership(uuid) to erp_app;
grant execute on function vendor.raise_cr_dispute(text, text, text, text, text, text) to erp_app;
grant execute on function vendor.accept_cr_dispute(uuid) to erp_app;
grant execute on function vendor.open_cr_disputes() to erp_app;
grant execute on function vendor.resolve_cr_dispute(uuid, boolean, text) to erp_app;
grant execute on function vendor.record_dispute_idp_outcome(uuid, boolean, jsonb) to erp_app;
grant execute on function vendor.dispute_related_tenants(uuid) to erp_app;
grant execute on function vendor.upheld_disputes_needing_idp() to erp_app;
grant execute on function vendor.unalerted_cr_disputes(integer) to erp_app;
grant execute on function vendor.mark_cr_disputes_alerted(uuid[]) to erp_app;
