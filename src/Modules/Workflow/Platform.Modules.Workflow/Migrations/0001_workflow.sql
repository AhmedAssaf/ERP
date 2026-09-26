create schema if not exists workflow;

create table workflow.workflow_definition (
    id         uuid        primary key,
    tenant_id  uuid        not null,
    name       text        not null,
    version    integer     not null,
    is_default boolean     not null,
    created_at timestamptz not null
);
create index ix_workflow_definition_tenant on workflow.workflow_definition (tenant_id);
create unique index ux_workflow_definition_one_default on workflow.workflow_definition (tenant_id) where is_default;

create table workflow.workflow_step (
    id            uuid          primary key,
    tenant_id     uuid          not null,
    definition_id uuid          not null references workflow.workflow_definition (id) on delete cascade,
    position      integer       not null,
    stage         text          not null,
    department    text          not null,
    rule          text          not null,
    actor_roles   text[]        not null,
    threshold     numeric(18,2) null,
    unique (definition_id, position)
);
create index ix_workflow_step_tenant on workflow.workflow_step (tenant_id, definition_id);

create table workflow.tender_workflow (
    id               uuid        primary key,
    tenant_id        uuid        not null,
    tender_id        uuid        not null,
    definition_id    uuid        not null,
    snapshot         jsonb       not null,
    snapshot_version integer     not null,
    state            text        not null,
    created_at       timestamptz not null,
    updated_at       timestamptz not null,
    unique (tenant_id, tender_id)
);

create table workflow.tender_workflow_step (
    id                 uuid    primary key,
    tenant_id          uuid    not null,
    tender_workflow_id uuid    not null references workflow.tender_workflow (id) on delete cascade,
    position           integer not null,
    stage              text    not null,
    department         text    not null,
    rule               text    not null,
    assigned_users     text[]  not null,
    status             text    not null,
    unique (tender_workflow_id, position)
);
create index ix_tender_workflow_step_tenant on workflow.tender_workflow_step (tenant_id, tender_workflow_id);

create table workflow.step_decision (
    id         uuid        primary key,
    tenant_id  uuid        not null,
    step_id    uuid        not null references workflow.tender_workflow_step (id) on delete cascade,
    user_id    text        not null,
    decision   text        not null,
    decided_at timestamptz not null,
    unique (step_id, user_id)
);
create index ix_step_decision_tenant on workflow.step_decision (tenant_id, step_id);

select platform.enable_tenant_rls('workflow', 'workflow_definition');
select platform.enable_tenant_rls('workflow', 'workflow_step');
select platform.enable_tenant_rls('workflow', 'tender_workflow');
select platform.enable_tenant_rls('workflow', 'tender_workflow_step');
select platform.enable_tenant_rls('workflow', 'step_decision');

grant usage on schema workflow to erp_app;
grant select, insert, update, delete on all tables in schema workflow to erp_app;
