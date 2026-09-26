create schema if not exists audit;

create table audit.events (
    id           uuid        primary key,
    tenant_id    uuid        not null,
    occurred_at  timestamptz not null default now(),
    actor_id     text        null,
    action       text        not null,
    subject_type text        not null,
    subject_id   text        null,
    data         jsonb       not null default '{}'::jsonb
);

create index ix_events_tenant_occurred on audit.events (tenant_id, occurred_at desc);

select platform.enable_tenant_rls('audit', 'events');

grant usage on schema audit to erp_app;
-- Append-only at the database level: no UPDATE or DELETE for the application role.
grant select, insert on audit.events to erp_app;
