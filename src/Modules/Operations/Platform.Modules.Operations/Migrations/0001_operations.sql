-- Platform-level operations data (F-51, F-60). No tenant_id: these rows are platform infrastructure facts, not
-- tenant events, so there is no RLS policy here (guarded instead by grants only, spec section 6).
create schema if not exists ops;

create table ops.health_results (
    id         uuid        primary key,
    component  text        not null,
    status     text        not null check (status in ('Healthy', 'Degraded', 'Unhealthy')),
    latency_ms integer     not null,
    checked_at timestamptz not null,
    message    text        null
);

-- The board and LatestAsync read the newest row per component.
create index ix_health_results_component_checked on ops.health_results (component, checked_at desc);

create table ops.incidents (
    id             uuid        primary key,
    component      text        not null,
    opened_at      timestamptz not null,
    closed_at      timestamptz null,
    last_message   text        null,
    notified_open  boolean     not null default false,
    notified_close boolean     not null default false
);

-- One open incident per component (F-60 opening rule): a second Unhealthy result while one is open updates it instead.
create unique index ux_incidents_one_open_per_component on ops.incidents (component) where closed_at is null;

create index ix_incidents_opened_at on ops.incidents (opened_at desc);

create table ops.platform_audit (
    id           uuid        primary key,
    occurred_at  timestamptz not null default now(),
    actor_id     text        null,
    action       text        not null,
    subject_type text        not null,
    subject_id   text        null,
    data         jsonb       not null default '{}'::jsonb
);

grant usage on schema ops to erp_app;
grant select, insert, update, delete on ops.health_results to erp_app;
grant select, insert, update, delete on ops.incidents to erp_app;
-- Append-only at the database level: no UPDATE or DELETE for the application role.
grant select, insert on ops.platform_audit to erp_app;
