-- Vendor slice (spec docs/superpowers/specs/2026-09-27-vendors-design.md section 2; ADR-0008, ADR-0010).
-- One vendor company across tenants: the company, its users, documents and consent ledger are platform-level rows
-- without tenant_id, under forced row-level security keyed on the vendor company (app.vendor_company_id, read by
-- platform.current_vendor_company()). What a tenant knows or decides lives in vendor.relationships, under the tenant
-- policy. Tenant staff reach the shared facts only through security-definer functions that require a relationship.

-- The security-definer functions below read and write across the vendor policies as their owner, the role running
-- migrations. The tables force row-level security, which binds a table owner too unless it is a superuser or has
-- BYPASSRLS; without that the functions would silently see nothing, so refuse to migrate instead.
do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create schema if not exists vendor;

create table vendor.companies (
    id            uuid        primary key,
    cr_number     text        not null,
    name_ar       text        not null,
    name_en       text        not null,
    vat_number    text        not null,
    address       text        null,
    contact_name  text        not null,
    contact_phone text        null,
    contact_email text        not null,
    created_at    timestamptz not null default now(),
    constraint ux_companies_cr_number unique (cr_number),
    constraint ck_companies_cr_number check (cr_number ~ '^[0-9]{10}$'),
    constraint ck_companies_vat_number check (vat_number ~ '^3[0-9]{13}3$'),
    constraint ck_companies_name_ar check (char_length(name_ar) between 1 and 200 and btrim(name_ar) <> ''),
    constraint ck_companies_name_en check (char_length(name_en) between 1 and 200 and btrim(name_en) <> ''),
    constraint ck_companies_contact_name check (char_length(contact_name) between 1 and 200 and btrim(contact_name) <> ''),
    constraint ck_companies_contact_email check (char_length(contact_email) <= 254 and contact_email like '_%@_%')
);

create table vendor.vendor_users (
    id                     uuid        primary key,
    company_id             uuid        not null references vendor.companies (id),
    user_id                text        not null,
    role                   text        not null,
    privacy_notice_version text        not null,
    privacy_accepted_at    timestamptz not null,
    created_at             timestamptz not null default now(),
    constraint ux_vendor_users_user_id unique (user_id),
    constraint ck_vendor_users_role check (role in ('vendor-admin'))
);
create index ix_vendor_users_company on vendor.vendor_users (company_id);

create table vendor.documents (
    id          uuid        primary key,
    company_id  uuid        not null references vendor.companies (id),
    type        text        not null,
    expires_on  date        not null,
    object_key  text        not null,
    sha256      text        not null,
    scan_status text        not null,
    is_current  boolean     not null,
    created_at  timestamptz not null default now(),
    constraint ck_documents_type check (type in ('cr_certificate', 'vat_certificate')),
    constraint ck_documents_sha256 check (sha256 ~ '^[a-f0-9]{64}$'),
    constraint ck_documents_scan_status check (scan_status in ('pending_scan', 'clean', 'infected'))
);
-- One current file per type (V-8); older files stay as history.
create unique index ux_documents_current on vendor.documents (company_id, type) where is_current;

create table vendor.relationships (
    tenant_id     uuid        not null,
    company_id    uuid        not null references vendor.companies (id),
    status        text        not null,
    first_seen_at timestamptz not null default now(),
    approved_by   text        null,
    primary key (tenant_id, company_id),
    constraint ck_relationships_status check (status in ('pending', 'approved')),
    constraint ck_relationships_approved_by check ((status = 'approved') = (approved_by is not null))
);
create index ix_relationships_company on vendor.relationships (company_id);

-- Platform reference data (V-13): maintained by migration until the platform console gets a screen. No real recipient
-- exists yet; the development seed adds a test one.
create table vendor.recipients (
    id      uuid primary key,
    name_ar text not null,
    name_en text not null
);

-- The consent ledger (ADR-0010): a grant is a row, a revocation is a new row; nothing is ever updated or deleted.
create table vendor.consent_events (
    id               uuid        primary key,
    company_id       uuid        not null references vendor.companies (id),
    recipient_id     uuid        not null references vendor.recipients (id),
    scope            text        not null,
    kind             text        not null,
    valid_from       date        null,
    valid_to         date        null,
    revokes_grant_id uuid        null,
    actor_id         text        not null,
    occurred_at      timestamptz not null default now(),
    constraint ux_consent_events_id_company unique (id, company_id),
    -- A revocation may only name a grant of the same company.
    constraint fk_consent_events_revokes foreign key (revokes_grant_id, company_id)
        references vendor.consent_events (id, company_id),
    constraint ck_consent_events_scope check (scope in ('award_records', 'po_records', 'profile_documents')),
    constraint ck_consent_events_kind check (kind in ('grant', 'revoke')),
    constraint ck_consent_events_shape check (
        (kind = 'grant' and valid_from is not null and valid_to is not null and valid_to >= valid_from and revokes_grant_id is null)
        or (kind = 'revoke' and revokes_grant_id is not null and valid_from is null and valid_to is null))
);
create index ix_consent_events_company on vendor.consent_events (company_id);
-- A grant is revoked at most once.
create unique index ux_consent_events_revokes on vendor.consent_events (revokes_grant_id) where revokes_grant_id is not null;

-- Row-level security: the vendor company for platform-level rows, the tenant for the relationship.
alter table vendor.companies enable row level security;
alter table vendor.companies force row level security;
create policy vendor_isolation on vendor.companies
    using (id = platform.current_vendor_company())
    with check (id = platform.current_vendor_company());

alter table vendor.vendor_users enable row level security;
alter table vendor.vendor_users force row level security;
create policy vendor_isolation on vendor.vendor_users
    using (company_id = platform.current_vendor_company())
    with check (company_id = platform.current_vendor_company());

alter table vendor.documents enable row level security;
alter table vendor.documents force row level security;
create policy vendor_isolation on vendor.documents
    using (company_id = platform.current_vendor_company())
    with check (company_id = platform.current_vendor_company());

alter table vendor.consent_events enable row level security;
alter table vendor.consent_events force row level security;
create policy vendor_isolation on vendor.consent_events
    using (company_id = platform.current_vendor_company())
    with check (company_id = platform.current_vendor_company());

select platform.enable_tenant_rls('vendor', 'relationships');

-- Grants: the narrowest set the slice needs. No DELETE anywhere; the CR number and ids never change.
grant usage on schema vendor to erp_app;
grant select on vendor.companies to erp_app;
grant update (name_ar, name_en, vat_number, address, contact_name, contact_phone, contact_email) on vendor.companies to erp_app;
grant select, insert on vendor.vendor_users to erp_app;
grant select, insert on vendor.documents to erp_app;
grant update (scan_status, is_current) on vendor.documents to erp_app;
grant select, insert on vendor.relationships to erp_app;
grant update (status, approved_by) on vendor.relationships to erp_app;
grant select on vendor.recipients to erp_app;
-- Append-only at the database level.
grant select, insert on vendor.consent_events to erp_app;

-- Whether a company with this CR number is already on the platform (V-6), asked before any vendor context exists.
create function vendor.cr_exists(p_cr_number text) returns boolean
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$ select exists (select 1 from vendor.companies c where c.cr_number = p_cr_number) $$;

-- Registration through a tenant host (F-11, V-7): the company, its first vendor admin with the accepted privacy notice
-- version (V-14), and a pending relationship with the host's tenant, in one call. A duplicate CR number or a user who
-- already belongs to a company raises unique_violation (23505); no tenant raises insufficient_privilege (42501).
create function vendor.register_company(
    p_cr_number text, p_name_ar text, p_name_en text, p_vat_number text, p_address text,
    p_contact_name text, p_contact_phone text, p_contact_email text,
    p_user_id text, p_privacy_notice_version text)
    returns uuid
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_tenant  uuid := platform.current_tenant();
    v_company uuid := gen_random_uuid();
begin
    if v_tenant is null then
        raise exception 'A vendor company is registered through a tenant host.' using errcode = 'insufficient_privilege';
    end if;

    insert into vendor.companies (id, cr_number, name_ar, name_en, vat_number, address, contact_name, contact_phone, contact_email)
    values (v_company, p_cr_number, p_name_ar, p_name_en, p_vat_number, p_address, p_contact_name, p_contact_phone, p_contact_email);

    insert into vendor.vendor_users (id, company_id, user_id, role, privacy_notice_version, privacy_accepted_at)
    values (gen_random_uuid(), v_company, p_user_id, 'vendor-admin', p_privacy_notice_version, now());

    insert into vendor.relationships (tenant_id, company_id, status)
    values (v_tenant, v_company, 'pending');

    return v_company;
end
$$;

-- The shared company facts for tenant staff (V-11): a row only while the connection's tenant has a relationship.
create function vendor.related_company(p_company_id uuid)
    returns table (
        id uuid, cr_number text, name_ar text, name_en text, vat_number text, address text,
        contact_name text, contact_phone text, contact_email text)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select c.id, c.cr_number, c.name_ar, c.name_en, c.vat_number, c.address, c.contact_name, c.contact_phone, c.contact_email
    from vendor.companies c
    where c.id = p_company_id
      and exists (select 1 from vendor.relationships r
                  where r.tenant_id = platform.current_tenant() and r.company_id = c.id)
$$;

-- The company's documents for tenant staff, under the same relationship rule. Only files that passed the virus scan
-- (V-10): a pending or infected file is never handed to a tenant.
create function vendor.related_documents(p_company_id uuid)
    returns table (
        id uuid, type text, expires_on date, object_key text, sha256 text, scan_status text, is_current boolean,
        created_at timestamptz)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, d.type, d.expires_on, d.object_key, d.sha256, d.scan_status, d.is_current, d.created_at
    from vendor.documents d
    where d.company_id = p_company_id
      and d.scan_status = 'clean'
      and exists (select 1 from vendor.relationships r
                  where r.tenant_id = platform.current_tenant() and r.company_id = d.company_id)
$$;

revoke all on function vendor.cr_exists(text) from public;
revoke all on function vendor.register_company(text, text, text, text, text, text, text, text, text, text) from public;
revoke all on function vendor.related_company(uuid) from public;
revoke all on function vendor.related_documents(uuid) from public;
grant execute on function vendor.cr_exists(text) to erp_app;
grant execute on function vendor.register_company(text, text, text, text, text, text, text, text, text, text) to erp_app;
grant execute on function vendor.related_company(uuid) to erp_app;
grant execute on function vendor.related_documents(uuid) to erp_app;
