-- W-24: the ASP.NET Core Data Protection key ring of Platform.Web, shared by every web instance and kept across restarts,
-- so a login cookie (and an antiforgery token, and Blazor's prerendered component state) issued by one instance is
-- accepted by another. One row per XML element Data Protection stores (a key or a revocation); it only ever adds.
--
-- A platform-level table with no tenant_id, like ops.platform_audit, and guarded the same way:
-- 1. Grants: erp_app may SELECT and INSERT, never UPDATE or DELETE, so the application cannot rewrite or drop a key.
-- 2. Forced row-level security: only a connection with no tenant, no vendor and no acting user sees or adds a row. The
--    web host's key repository opens its own connections that never carry a request's context; every EF Core
--    connection opened for a request does carry one (TenantConnectionInterceptor), so a query injected there cannot
--    read the key material. Until a separate worker role exists (W-36), "no context" also matches an anonymous request
--    on the platform host.
-- 3. At rest, outside Development and Testing, each key is encrypted with the certificate in DataProtection:CertificatePath
--    before it is stored here, so a copy of the database or of a backup alone cannot forge a cookie.
create table if not exists platform.data_protection_keys (
    id            bigint      generated always as identity primary key,
    friendly_name text        not null,
    xml           text        not null,
    created_at    timestamptz not null default now()
);

grant select, insert on platform.data_protection_keys to erp_app;

alter table platform.data_protection_keys enable row level security;
alter table platform.data_protection_keys force row level security;
drop policy if exists data_protection_keys_without_context on platform.data_protection_keys;
create policy data_protection_keys_without_context on platform.data_protection_keys
    using (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null)
    with check (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null);

comment on table platform.data_protection_keys is
    'Data Protection key ring of Platform.Web (W-24), application name waslabid-web. erp_app reads and adds rows only on a connection without a tenant, vendor or user context, and never updates or deletes them.';
