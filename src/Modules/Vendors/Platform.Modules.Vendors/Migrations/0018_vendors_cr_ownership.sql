-- W-33 (pentest P-4, CR squatting): whoever registers a CR number first owns the company on every tenant (ADR-0008), so
-- ownership is verified before the company's first approval by any tenant, and the real company has a dispute path.
--
-- 1. vendor.ownership_settings: the platform's one setting, the check method (manual by default, or wathq). Platform
--    reference data like vendor.recipients: readable by erp_app, no row-level security, changed only through
--    vendor.set_ownership_method by a session with neither a tenant nor a vendor context (the platform console).
-- 2. vendor.ownership_verifications: one row per verified company (who registered it, how, by whom, the note). Forced
--    row-level security keyed on the company like every platform-level vendor table; erp_app holds no grant at all, so
--    staff read it only through vendor.related_ownership, which never tells one tenant which tenant verified (ADR-0008).
--    There is deliberately no tenant_id column (the verification is shared by every tenant); the column naming the
--    verifying tenant is verified_in_tenant and is never returned to another tenant.
-- 3. vendor.cr_disputes: a claim by a signed-in person that a company registered under a CR number is theirs. Forced
--    row-level security: only a session without a tenant or vendor context (the platform console) reads rows; a claimant
--    lists their own through vendor.my_cr_disputes. Rows change only through security-definer functions.
-- 4. vendor.approve_relationship now refuses a pending company whose ownership is not verified, or that has an open
--    dispute, under the constraint names ck_relationships_ownership_verified and ck_relationships_not_disputed.
--
-- Every security-definer function states who may call it (ADR-0012 point 4): staff functions refuse a vendor context and
-- require an active contracts officer or tenant admin of the host tenant who is no vendor user; the claimant's function
-- needs a tenant host, an acting user and no vendor context; the platform functions refuse any tenant or vendor context
-- and need an acting user. Until a separate worker role exists (W-36), "no context" also matches the worker's sessions.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

-- 1. The method --------------------------------------------------------------------------------------------------------

create table vendor.ownership_settings (
    id         boolean     primary key default true,
    method     text        not null,
    changed_by text        null,
    changed_at timestamptz not null default now(),
    constraint ck_ownership_settings_single check (id),
    constraint ck_ownership_settings_method check (method in ('manual', 'wathq'))
);
insert into vendor.ownership_settings (id, method) values (true, 'manual');
grant select on vendor.ownership_settings to erp_app;

comment on table vendor.ownership_settings is
    'W-33: the platform''s CR ownership check method (one row). Platform reference data without row-level security, like vendor.recipients; erp_app may only read it, and vendor.set_ownership_method changes it for a platform console session.';

-- Returns the method before the change.
create function vendor.set_ownership_method(p_method text)
    returns text
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_user text := platform.current_user_id();
    v_old  text;
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null or v_user is null then
        raise exception 'The ownership check method is changed by a platform admin in the platform console.'
            using errcode = 'insufficient_privilege';
    end if;

    select s.method into v_old from vendor.ownership_settings s where s.id for update;
    update vendor.ownership_settings set method = p_method, changed_by = v_user, changed_at = now() where id;
    return v_old;
end
$$;

-- 2. Verifications -----------------------------------------------------------------------------------------------------

create table vendor.ownership_verifications (
    company_id         uuid        primary key references vendor.companies (id),
    method             text        not null,
    registrant_user_id text        not null,
    verified_by        text        not null,
    verified_at        timestamptz not null default now(),
    verified_in_tenant uuid        null,
    note               text        not null,
    constraint ck_ownership_verifications_method check (method in ('manual', 'wathq', 'dispute')),
    constraint ck_ownership_verifications_note check (char_length(note) between 1 and 1000 and btrim(note) <> ''),
    -- An officer verifies inside a tenant; a platform admin upholding a dispute verifies for the platform.
    constraint ck_ownership_verifications_tenant check ((method = 'dispute') = (verified_in_tenant is null))
);

alter table vendor.ownership_verifications enable row level security;
alter table vendor.ownership_verifications force row level security;
create policy vendor_isolation on vendor.ownership_verifications
    using (company_id = platform.current_vendor_company())
    with check (company_id = platform.current_vendor_company());

-- 3. Disputes ----------------------------------------------------------------------------------------------------------

create table vendor.cr_disputes (
    id                     uuid        primary key,
    company_id             uuid        not null references vendor.companies (id),
    claimant_user_id       text        not null,
    claimant_email         text        not null,
    claimant_name          text        not null,
    statement              text        not null,
    privacy_notice_version text        not null,
    privacy_notice_culture text        not null,
    raised_on_tenant       uuid        not null,
    raised_at              timestamptz not null default now(),
    status                 text        not null default 'open',
    resolved_by            text        null,
    resolved_at            timestamptz null,
    resolution_note        text        null,
    removed_user_ids       text[]      null,
    constraint ck_cr_disputes_status check (status in ('open', 'upheld', 'rejected')),
    constraint ck_cr_disputes_resolution check (
        (status = 'open' and resolved_by is null and resolved_at is null and resolution_note is null and removed_user_ids is null)
        or (status = 'rejected' and resolved_by is not null and resolved_at is not null and resolution_note is not null and removed_user_ids is null)
        or (status = 'upheld' and resolved_by is not null and resolved_at is not null and resolution_note is not null and removed_user_ids is not null)),
    constraint ck_cr_disputes_resolution_note check (
        resolution_note is null or (char_length(resolution_note) between 1 and 1000 and btrim(resolution_note) <> '')),
    constraint ck_cr_disputes_claimant_email check (char_length(claimant_email) <= 254 and claimant_email like '_%@_%'),
    constraint ck_cr_disputes_claimant_name check (char_length(claimant_name) between 1 and 200 and btrim(claimant_name) <> ''),
    constraint ck_cr_disputes_statement check (char_length(statement) between 1 and 2000 and btrim(statement) <> ''),
    constraint ck_cr_disputes_privacy_notice_version check (
        char_length(privacy_notice_version) between 1 and 40 and btrim(privacy_notice_version) <> ''),
    constraint ck_cr_disputes_privacy_notice_culture check (privacy_notice_culture in ('ar-SA', 'en-US'))
);
-- One open dispute per claimant and company.
create unique index ux_cr_disputes_open on vendor.cr_disputes (company_id, claimant_user_id) where status = 'open';
create index ix_cr_disputes_claimant on vendor.cr_disputes (claimant_user_id);

alter table vendor.cr_disputes enable row level security;
alter table vendor.cr_disputes force row level security;
create policy cr_dispute_access on vendor.cr_disputes
    for select
    using (platform.current_tenant() is null and platform.current_vendor_company() is null);
grant select on vendor.cr_disputes to erp_app;

-- A staff member of the tenant who may decide about vendors: the same rule as vendor.approve_relationship (0016).
create function vendor.require_vendor_manager()
    returns void
    language plpgsql
    stable
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
end
$$;

-- The company's vendor admin whose ownership an officer checks: the earliest vendor-admin row of the company.
create function vendor.company_registrant(p_company_id uuid)
    returns text
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select u.user_id from vendor.vendor_users u
    where u.company_id = p_company_id and u.role = 'vendor-admin'
    order by u.created_at, u.id
    limit 1
$$;

-- What the tenant's staff know about a related company's ownership. verified_here is true only when an officer of this
-- tenant verified it; which other tenant verified it is never told (ADR-0008). No rows without a relationship, or in a
-- vendor session.
create function vendor.related_ownership(p_company_id uuid)
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
           exists (select 1 from vendor.cr_disputes d where d.company_id = c.id and d.status = 'open'),
           exists (select 1 from vendor.documents d
                   where d.company_id = c.id and d.type = 'cr_certificate' and d.is_current and d.scan_status = 'clean')
    from vendor.companies c
    left join vendor.ownership_verifications v on v.company_id = c.id
    where c.id = p_company_id
      and platform.current_vendor_company() is null
      and exists (select 1 from vendor.relationships r
                  where r.tenant_id = platform.current_tenant() and r.company_id = c.id)
$$;

-- An officer confirms that the current clean CR certificate names the registering person, or that an authorisation backs
-- them. True when this call verified the company; false when it was verified already (the first record stays).
create function vendor.verify_ownership(p_company_id uuid, p_method text, p_note text)
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

    if exists (select 1 from vendor.cr_disputes d where d.company_id = p_company_id and d.status = 'open') then
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

-- 4. Approval needs a verified owner and no open dispute (same signature as 0016, so the grants stay).
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

    if exists (select 1 from vendor.relationships r
               where r.tenant_id = v_tenant and r.company_id = p_company_id and r.status = 'pending') then
        if not exists (select 1 from vendor.ownership_verifications v where v.company_id = p_company_id) then
            raise exception 'The company''s ownership is verified before its first approval.'
                using errcode = 'check_violation', constraint = 'ck_relationships_ownership_verified', table = 'relationships', schema = 'vendor';
        end if;

        if exists (select 1 from vendor.cr_disputes d where d.company_id = p_company_id and d.status = 'open') then
            raise exception 'The company''s ownership is under review by WaslaBid.'
                using errcode = 'check_violation', constraint = 'ck_relationships_not_disputed', table = 'relationships', schema = 'vendor';
        end if;
    end if;

    update vendor.relationships
    set status = 'approved', approved_by = v_user
    where tenant_id = v_tenant and company_id = p_company_id and status = 'pending';
    return found;
end
$$;

-- A signed-in person on a tenant host claims the company registered under p_cr_number. Null when no company has that
-- number. The claimant is the acting user; the email and name come from the caller's verified token.
create function vendor.raise_cr_dispute(
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

    select c.id into v_company from vendor.companies c where c.cr_number = p_cr_number;
    if v_company is null then
        return null;
    end if;

    if (select count(*) from vendor.cr_disputes d where d.claimant_user_id = v_user and d.status = 'open') >= 3 then
        raise exception 'A person has at most three open disputes.'
            using errcode = 'check_violation', constraint = 'ck_cr_disputes_open_limit', table = 'cr_disputes', schema = 'vendor';
    end if;

    insert into vendor.cr_disputes (
        id, company_id, claimant_user_id, claimant_email, claimant_name, statement,
        privacy_notice_version, privacy_notice_culture, raised_on_tenant)
    values (v_id, v_company, v_user, p_claimant_email, p_claimant_name, p_statement,
            p_privacy_notice_version, p_privacy_notice_culture, v_tenant);
    return v_id;
end
$$;

-- The acting user's own disputes, newest first (the claimant's page).
create function vendor.my_cr_disputes()
    returns table (id uuid, cr_number text, raised_at timestamptz, status text)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, c.cr_number, d.raised_at, d.status
    from vendor.cr_disputes d
    join vendor.companies c on c.id = d.company_id
    where d.claimant_user_id = platform.current_user_id()
      and platform.current_user_id() is not null
      and platform.current_vendor_company() is null
    order by d.raised_at desc, d.id
$$;

-- The open disputes, oldest first, for the platform console. No rows for a session with a tenant or vendor context.
create function vendor.open_cr_disputes()
    returns table (
        id uuid, company_id uuid, cr_number text, name_ar text, name_en text,
        claimant_user_id text, claimant_email text, claimant_name text, statement text,
        raised_on_tenant uuid, raised_at timestamptz, registrant_user_id text, ownership_method text)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, d.company_id, c.cr_number, c.name_ar, c.name_en,
           d.claimant_user_id, d.claimant_email, d.claimant_name, d.statement,
           d.raised_on_tenant, d.raised_at, vendor.company_registrant(c.id), v.method
    from vendor.cr_disputes d
    join vendor.companies c on c.id = d.company_id
    left join vendor.ownership_verifications v on v.company_id = d.company_id
    where d.status = 'open'
      and platform.current_tenant() is null
      and platform.current_vendor_company() is null
      and platform.current_user_id() is not null
    order by d.raised_at, d.id
$$;

-- A platform admin closes an open dispute. Upheld: the company's vendor users are removed, the claimant becomes its
-- vendor admin with the privacy notice they accepted when they raised it, and its ownership is recorded as verified by
-- the platform admin (method dispute). Rejected: only the dispute changes. The consent ledger, documents and
-- relationships stay with the company (ADR-0010). No row when the dispute does not exist or is no longer open.
create function vendor.resolve_cr_dispute(p_dispute_id uuid, p_uphold boolean, p_note text)
    returns table (company_id uuid, claimant_user_id text, removed_user_ids text[])
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_user    text := platform.current_user_id();
    v_dispute vendor.cr_disputes%rowtype;
    v_removed text[];
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null or v_user is null then
        raise exception 'A dispute is resolved by a platform admin in the platform console.' using errcode = 'insufficient_privilege';
    end if;

    select * into v_dispute from vendor.cr_disputes d where d.id = p_dispute_id and d.status = 'open' for update;
    if not found then
        return;
    end if;

    if not p_uphold then
        update vendor.cr_disputes d
        set status = 'rejected', resolved_by = v_user, resolved_at = now(), resolution_note = p_note
        where d.id = p_dispute_id;
        return query select v_dispute.company_id, v_dispute.claimant_user_id, null::text[];
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

    insert into vendor.ownership_verifications (company_id, method, registrant_user_id, verified_by, verified_in_tenant, note)
    values (v_dispute.company_id, 'dispute', v_dispute.claimant_user_id, v_user, null, p_note)
    on conflict on constraint ownership_verifications_pkey do update
        set method = 'dispute', registrant_user_id = excluded.registrant_user_id, verified_by = excluded.verified_by,
            verified_at = now(), verified_in_tenant = null, note = excluded.note;

    update vendor.cr_disputes d
    set status = 'upheld', resolved_by = v_user, resolved_at = now(), resolution_note = p_note, removed_user_ids = v_removed
    where d.id = p_dispute_id;

    return query select v_dispute.company_id, v_dispute.claimant_user_id, v_removed;
end
$$;

revoke all on function vendor.set_ownership_method(text) from public;
revoke all on function vendor.require_vendor_manager() from public;
revoke all on function vendor.company_registrant(uuid) from public;
revoke all on function vendor.related_ownership(uuid) from public;
revoke all on function vendor.verify_ownership(uuid, text, text) from public;
revoke all on function vendor.approve_relationship(uuid) from public;
revoke all on function vendor.raise_cr_dispute(text, text, text, text, text, text) from public;
revoke all on function vendor.my_cr_disputes() from public;
revoke all on function vendor.open_cr_disputes() from public;
revoke all on function vendor.resolve_cr_dispute(uuid, boolean, text) from public;
-- require_vendor_manager and company_registrant are helpers of the functions above; erp_app never calls them directly.
grant execute on function vendor.set_ownership_method(text) to erp_app;
grant execute on function vendor.related_ownership(uuid) to erp_app;
grant execute on function vendor.verify_ownership(uuid, text, text) to erp_app;
grant execute on function vendor.approve_relationship(uuid) to erp_app;
grant execute on function vendor.raise_cr_dispute(text, text, text, text, text, text) to erp_app;
grant execute on function vendor.my_cr_disputes() to erp_app;
grant execute on function vendor.open_cr_disputes() to erp_app;
grant execute on function vendor.resolve_cr_dispute(uuid, boolean, text) to erp_app;
