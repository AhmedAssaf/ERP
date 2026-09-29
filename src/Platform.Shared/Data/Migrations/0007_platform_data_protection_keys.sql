-- W-24: the ASP.NET Core Data Protection key ring of Platform.Web, shared by every web instance and kept across restarts,
-- so a login cookie (and an antiforgery token, and Blazor's prerendered component state) issued by one instance is
-- accepted by another. One row per XML element Data Protection stores (a key or a revocation); it only ever adds.
--
-- Whoever can add a key can forge any session, so the protection is who may write here, not what a session claims:
-- 1. Its own role. Only erp_key_ring may read or add rows (SELECT and INSERT, never UPDATE, DELETE or TRUNCATE), and only
--    the web host's key-ring pool connects as it (ConnectionStrings:KeyRing, a secret). erp_app, which every module,
--    the worker and Hangfire use, and which any injected SQL would run as, has no right on the table at all. The
--    migration creates the role without a login; the migrator gives it one from ConnectionStrings:KeyRing (N-10: the
--    password is never in a script).
-- 2. At rest, outside Development and Testing, each key is encrypted with the certificate in DataProtection:CertificatePath,
--    so a copy of the database or of a backup alone cannot forge a cookie; the host then also ignores any key stored
--    without encryption.
-- 3. Defence in depth only: forced row-level security admits a row only on a connection with no tenant, vendor or user
--    context. The key-ring pool never sets one. This is not the protection: a session can clear its own settings.
do $$
begin
    if not exists (select 1 from pg_roles where rolname = 'erp_key_ring') then
        create role erp_key_ring nologin noinherit;
    end if;
end
$$;

do $$
begin
    execute format('grant connect on database %I to erp_key_ring', current_database());
end
$$;

create table if not exists platform.data_protection_keys (
    id            bigint      generated always as identity primary key,
    friendly_name text        not null,
    xml           text        not null,
    created_at    timestamptz not null default now()
);

revoke all on platform.data_protection_keys from public;
revoke all on platform.data_protection_keys from erp_app;
grant usage on schema platform to erp_key_ring;
grant execute on function platform.current_tenant() to erp_key_ring;
grant execute on function platform.current_vendor_company() to erp_key_ring;
grant execute on function platform.current_user_id() to erp_key_ring;
grant select, insert on platform.data_protection_keys to erp_key_ring;

alter table platform.data_protection_keys enable row level security;
alter table platform.data_protection_keys force row level security;
drop policy if exists data_protection_keys_without_context on platform.data_protection_keys;
create policy data_protection_keys_without_context on platform.data_protection_keys
    using (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null)
    with check (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null);

comment on table platform.data_protection_keys is
    'Data Protection key ring of Platform.Web (W-24), application name waslabid-web. Only erp_key_ring (the web host''s key-ring pool) may select and insert; erp_app has no right on it.';
