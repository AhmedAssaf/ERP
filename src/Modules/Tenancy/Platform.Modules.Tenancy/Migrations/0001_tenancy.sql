-- Platform-level tables: the one deliberate exception to tenant RLS (spec sections 2.1 and 7). They are read before a
-- tenant is known, so access is limited by grants: erp_app has no table rights, only the resolve_host function.
create schema if not exists tenancy;

create table tenancy.tenants (
    id                 uuid        primary key,
    slug               text        not null unique check (slug ~ '^[a-z0-9-]{2,40}$'),
    keycloak_org_alias text        not null unique,
    default_culture    text        not null default 'ar-SA' check (default_culture in ('ar-SA', 'en-US')),
    portal_name        text        not null,
    primary_color      text        not null check (primary_color ~ '^#[0-9A-Fa-f]{6}$'),
    logo_url           text        null,
    created_at         timestamptz not null default now()
);

create table tenancy.tenant_hosts (
    host      text primary key check (host = lower(host)),
    tenant_id uuid not null references tenancy.tenants (id)
);

create function tenancy.resolve_host(p_host text)
    returns table (
        tenant_id uuid, slug text, keycloak_org_alias text, default_culture text,
        portal_name text, primary_color text, logo_url text)
    language sql
    stable
    security definer
    set search_path = tenancy, pg_temp
as $$
    select t.id, t.slug, t.keycloak_org_alias, t.default_culture, t.portal_name, t.primary_color, t.logo_url
    from tenancy.tenant_hosts h
    join tenancy.tenants t on t.id = h.tenant_id
    where h.host = lower(p_host)
$$;

revoke all on function tenancy.resolve_host(text) from public;
grant usage on schema tenancy to erp_app;
grant execute on function tenancy.resolve_host(text) to erp_app;
