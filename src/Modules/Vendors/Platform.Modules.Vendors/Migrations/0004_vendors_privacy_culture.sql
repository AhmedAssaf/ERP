-- The culture the privacy notice was shown in (vendor plan task 2 review, V-14): the version alone does not say which
-- text a vendor user read, since each version is published in Arabic and English. Stored with the acceptance.
-- Rows accepted before this migration keep NULL (the culture was not recorded then); every new acceptance names one.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

alter table vendor.vendor_users add column privacy_notice_culture text null;
alter table vendor.vendor_users
    add constraint ck_vendor_users_privacy_notice_culture check (privacy_notice_culture in ('ar-SA', 'en-US'));

-- register_company takes the culture as its last parameter; the signature of 0003 goes, so there is one signature.
-- No new table, so no new row-level security policy: vendor_users keeps the company policy of 0001.
drop function vendor.register_company(text, text, text, text, text, text, text, text, text);

create function vendor.register_company(
    p_cr_number text, p_name_ar text, p_name_en text, p_vat_number text, p_address text,
    p_contact_name text, p_contact_phone text, p_contact_email text,
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
    v_company uuid := gen_random_uuid();
begin
    if v_tenant is null then
        raise exception 'A vendor company is registered through a tenant host.' using errcode = 'insufficient_privilege';
    end if;

    if v_user is null then
        raise exception 'A vendor company is registered by a signed-in user.' using errcode = 'insufficient_privilege';
    end if;

    if p_privacy_notice_culture is null then
        raise exception 'The culture the privacy notice was shown in is required.' using errcode = 'not_null_violation';
    end if;

    insert into vendor.companies (id, cr_number, name_ar, name_en, vat_number, address, contact_name, contact_phone, contact_email)
    values (v_company, p_cr_number, p_name_ar, p_name_en, p_vat_number, p_address, p_contact_name, p_contact_phone, p_contact_email);

    insert into vendor.vendor_users (id, company_id, user_id, role, privacy_notice_version, privacy_notice_culture, privacy_accepted_at)
    values (gen_random_uuid(), v_company, v_user, 'vendor-admin', p_privacy_notice_version, p_privacy_notice_culture, now());

    insert into vendor.relationships (tenant_id, company_id, status)
    values (v_tenant, v_company, 'pending');

    return v_company;
end
$$;

revoke all on function vendor.register_company(text, text, text, text, text, text, text, text, text, text) from public;
grant execute on function vendor.register_company(text, text, text, text, text, text, text, text, text, text) to erp_app;
