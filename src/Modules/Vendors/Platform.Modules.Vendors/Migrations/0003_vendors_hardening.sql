-- Vendor data hardening (review of vendor plan tasks 1-2).
-- 1. Relationships change only through controlled functions: the application role keeps SELECT on
--    vendor.relationships but loses INSERT and UPDATE, so a tenant connection can no longer relate itself to any
--    company (and so read it through related_company/related_documents) or approve one by hand.
-- 2. The acting user comes from the session (app.user_id, platform.current_user_id(), platform migration 0005), never
--    from a parameter: register_company loses its user parameter; approve_relationship records the acting user.
-- 3. A consent revocation must name a grant, not another revocation.
-- 4. The accepted privacy notice version is 1 to 40 characters and not blank.

-- The functions below run as their owner, the role running migrations, across forced row-level security (see 0001).
do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

-- 1. No direct writes to relationships. Revoking the table privilege revokes the column privileges of 0001 too; both
--    are named so the intent is explicit.
revoke insert, update on vendor.relationships from erp_app;
revoke update (status, approved_by) on vendor.relationships from erp_app;

-- 2. Registration takes its first vendor admin from the session. A caller with no acting user is refused (42501), as
--    one with no tenant is.
drop function vendor.register_company(text, text, text, text, text, text, text, text, text, text);

create function vendor.register_company(
    p_cr_number text, p_name_ar text, p_name_en text, p_vat_number text, p_address text,
    p_contact_name text, p_contact_phone text, p_contact_email text,
    p_privacy_notice_version text)
    returns uuid
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_tenant  uuid := platform.current_tenant();
    v_user    text := platform.current_user_id();
    v_company uuid := gen_random_uuid();
begin
    if v_tenant is null then
        raise exception 'A vendor company is registered through a tenant host.' using errcode = 'insufficient_privilege';
    end if;

    if v_user is null then
        raise exception 'A vendor company is registered by a signed-in user.' using errcode = 'insufficient_privilege';
    end if;

    insert into vendor.companies (id, cr_number, name_ar, name_en, vat_number, address, contact_name, contact_phone, contact_email)
    values (v_company, p_cr_number, p_name_ar, p_name_en, p_vat_number, p_address, p_contact_name, p_contact_phone, p_contact_email);

    insert into vendor.vendor_users (id, company_id, user_id, role, privacy_notice_version, privacy_accepted_at)
    values (gen_random_uuid(), v_company, v_user, 'vendor-admin', p_privacy_notice_version, now());

    insert into vendor.relationships (tenant_id, company_id, status)
    values (v_tenant, v_company, 'pending');

    return v_company;
end
$$;

-- A vendor meets another tenant (the join flow, vendor plan task 5): a pending relationship between the connection's
-- tenant and its vendor company. The vendor context is set only after the Vendor policy passed, and the acting user must
-- be a user of that company, so the caller proves it acts for the company. Joining again changes nothing (an approved
-- relationship stays approved). Without a tenant, a vendor context or a matching acting user: 42501.
create function vendor.join_tenant()
    returns void
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_tenant  uuid := platform.current_tenant();
    v_company uuid := platform.current_vendor_company();
    v_user    text := platform.current_user_id();
begin
    if v_tenant is null or v_company is null or v_user is null
       or not exists (select 1 from vendor.vendor_users u where u.company_id = v_company and u.user_id = v_user) then
        raise exception 'A vendor joins a tenant as a signed-in user of its company, on that tenant''s host.'
            using errcode = 'insufficient_privilege';
    end if;

    insert into vendor.relationships (tenant_id, company_id, status)
    values (v_tenant, v_company, 'pending')
    on conflict (tenant_id, company_id) do nothing;
end
$$;

-- An officer or tenant admin approves a pending vendor (V-7, V-11). The application calls it only after its
-- ContractsOfficer or TenantAdmin policy passed; the function records the acting user as the approver. True when a
-- pending relationship of the connection's tenant was approved; false when there is none (no relationship, or already
-- approved, whose first approver stays on record). Without a tenant or an acting user: 42501.
create function vendor.approve_relationship(p_company_id uuid)
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

    update vendor.relationships
    set status = 'approved', approved_by = v_user
    where tenant_id = v_tenant and company_id = p_company_id and status = 'pending';
    return found;
end
$$;

revoke all on function vendor.register_company(text, text, text, text, text, text, text, text, text) from public;
revoke all on function vendor.join_tenant() from public;
revoke all on function vendor.approve_relationship(uuid) from public;
grant execute on function vendor.register_company(text, text, text, text, text, text, text, text, text) to erp_app;
grant execute on function vendor.join_tenant() to erp_app;
grant execute on function vendor.approve_relationship(uuid) to erp_app;

-- 3. A revocation names a grant. PostgreSQL cannot reference a partial unique index from a foreign key, so the kind
--    the revocation expects ('grant') is a generated column and the key includes the referenced row's kind. Grants
--    leave revokes_kind NULL, which the key does not check (MATCH SIMPLE). The same-company key of 0001 stays.
alter table vendor.consent_events
    add column revokes_kind text generated always as (case when revokes_grant_id is not null then 'grant' end) stored;
alter table vendor.consent_events
    add constraint ux_consent_events_id_company_kind unique (id, company_id, kind);
alter table vendor.consent_events
    add constraint fk_consent_events_revokes_grant foreign key (revokes_grant_id, company_id, revokes_kind)
        references vendor.consent_events (id, company_id, kind);

-- 4. The privacy notice version a vendor accepted (V-14): 1 to 40 characters with at least one that is not whitespace.
alter table vendor.vendor_users
    add constraint ck_vendor_users_privacy_notice_version
        check (char_length(privacy_notice_version) between 1 and 40 and privacy_notice_version ~ '[^[:space:]]');
