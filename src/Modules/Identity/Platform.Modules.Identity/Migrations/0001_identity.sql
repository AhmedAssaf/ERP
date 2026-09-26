-- Tenant staff and their roles (F-07, spec D-3 and 4.1). Keycloak holds identity and organization membership; roles per
-- tenant live here, under forced row-level security like every tenant-owned table.
create schema if not exists identity;

create table identity.members (
    id           uuid        primary key,
    tenant_id    uuid        not null,
    -- The Keycloak sub. Null only until first sign-in for a row created without it (the development seed), which the
    -- members claims transformation binds by verified email; an active member always has it.
    user_id      text        null,
    email        text        not null check (email = lower(email)),
    display_name text        not null,
    roles        text[]      not null default '{}'
                             check (roles <@ array['tenant-admin', 'contracts-officer', 'technical-evaluator', 'finance-approver']::text[]),
    status       text        not null check (status in ('invited', 'active')),
    invited_at   timestamptz not null,
    activated_at timestamptz null,
    check (status = 'invited' or (user_id is not null and activated_at is not null)),
    unique (tenant_id, user_id),
    unique (tenant_id, email)
);

select platform.enable_tenant_rls('identity', 'members');

grant usage on schema identity to erp_app;
grant select, insert, update, delete on identity.members to erp_app;
