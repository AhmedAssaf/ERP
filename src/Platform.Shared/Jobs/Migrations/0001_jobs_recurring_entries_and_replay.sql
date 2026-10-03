-- W-42 (ADR-0012 addendum, residual of W-36): Hangfire's recurring entries are the worker's alone, and a signed job's
-- nonce runs under one job id only. The migrator applies this set (journal module "jobs") right after it installs or
-- upgrades Hangfire's tables (JobsModule.InstallSchema), since it secures those tables.
--
-- 1. Recurring entries. erp_app (the web host, and any SQL injected as it) keeps reading and writing Hangfire's tables to
--    enqueue, re-run a failed job from the console and show the dashboard, but may no longer write the rows that schedule
--    the worker's recurring jobs, so it cannot delete or re-time the health checks, scans, cleanups and alerts:
--    - hangfire.hash, keys 'recurring-job:<id>' (each recurring job's definition and next execution);
--    - hangfire.set, key 'recurring-jobs' (the ids, scored by next execution);
--    - hangfire.lock, every resource but a job's state lock ('hangfire:job:<id>:state-lock': Hangfire's name prefixed with
--      the schema by Hangfire.PostgreSql, which enqueueing and the console's re-run take): the recurring scheduler's, the expiration manager's and a job's DisableConcurrentExecution locks
--      are the worker's. A state lock erp_app writes must say it was acquired now (within five minutes), so it expires
--      after Hangfire's lock timeout instead of holding a job's state forever.
--    erp_worker (member of erp_app) and the owner (BYPASSRLS) are unrestricted. erp_app still reads every row (the dashboard
--    shows the recurring jobs). Forced row-level security; Hangfire.PostgreSql's own statements (update then insert on
--    hash, insert on conflict on set and lock) work unchanged for both roles.
-- 2. platform.job_nonces: the first run of a signed job binds its nonce to its job id (JobReplayLedger), so a row copied
--    with its signature into a new job is refused. Only erp_worker reads, inserts and deletes, without any context;
--    erp_app has no right on it.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the jobs migrations as a role with BYPASSRLS (or the superuser): Hangfire''s install and upgrades run as the owner under forced row-level security.';
    end if;

    if not exists (select 1 from pg_roles where rolname = 'erp_worker') then
        raise exception 'Run the platform migrations first: platform 0008 creates erp_worker.';
    end if;

    if to_regclass('hangfire.hash') is null or to_regclass('hangfire.set') is null or to_regclass('hangfire.lock') is null then
        raise exception 'Install Hangfire''s tables first (the migrator does, right before this set).';
    end if;
end
$$;

-- 1. Recurring entries.
alter table hangfire.hash enable row level security;
alter table hangfire.hash force row level security;
drop policy if exists hash_read on hangfire.hash;
drop policy if exists hash_app_insert on hangfire.hash;
drop policy if exists hash_app_update on hangfire.hash;
drop policy if exists hash_app_delete on hangfire.hash;
drop policy if exists hash_worker on hangfire.hash;
create policy hash_read on hangfire.hash
    for select
    to erp_app
    using (true);
create policy hash_app_insert on hangfire.hash
    for insert
    to erp_app
    with check (not starts_with(key, 'recurring-job:'));
create policy hash_app_update on hangfire.hash
    for update
    to erp_app
    using (not starts_with(key, 'recurring-job:'))
    with check (not starts_with(key, 'recurring-job:'));
create policy hash_app_delete on hangfire.hash
    for delete
    to erp_app
    using (not starts_with(key, 'recurring-job:'));
create policy hash_worker on hangfire.hash
    for all
    to erp_worker
    using (true)
    with check (true);

alter table hangfire.set enable row level security;
alter table hangfire.set force row level security;
drop policy if exists set_read on hangfire.set;
drop policy if exists set_app_insert on hangfire.set;
drop policy if exists set_app_update on hangfire.set;
drop policy if exists set_app_delete on hangfire.set;
drop policy if exists set_worker on hangfire.set;
create policy set_read on hangfire.set
    for select
    to erp_app
    using (true);
create policy set_app_insert on hangfire.set
    for insert
    to erp_app
    with check (key <> 'recurring-jobs');
create policy set_app_update on hangfire.set
    for update
    to erp_app
    using (key <> 'recurring-jobs')
    with check (key <> 'recurring-jobs');
create policy set_app_delete on hangfire.set
    for delete
    to erp_app
    using (key <> 'recurring-jobs');
create policy set_worker on hangfire.set
    for all
    to erp_worker
    using (true)
    with check (true);

alter table hangfire.lock enable row level security;
alter table hangfire.lock force row level security;
drop policy if exists lock_read on hangfire.lock;
drop policy if exists lock_app_insert on hangfire.lock;
drop policy if exists lock_app_update on hangfire.lock;
drop policy if exists lock_app_delete on hangfire.lock;
drop policy if exists lock_worker on hangfire.lock;
create policy lock_read on hangfire.lock
    for select
    to erp_app
    using (true);
create policy lock_app_insert on hangfire.lock
    for insert
    to erp_app
    with check (resource like 'hangfire:job:%:state-lock' and (acquired is null or acquired <= now() + interval '5 minutes'));
create policy lock_app_update on hangfire.lock
    for update
    to erp_app
    using (resource like 'hangfire:job:%:state-lock')
    with check (resource like 'hangfire:job:%:state-lock' and (acquired is null or acquired <= now() + interval '5 minutes'));
create policy lock_app_delete on hangfire.lock
    for delete
    to erp_app
    using (resource like 'hangfire:job:%:state-lock');
create policy lock_worker on hangfire.lock
    for all
    to erp_worker
    using (true)
    with check (true);

-- 2. The replay ledger.
create table if not exists platform.job_nonces (
    nonce        uuid        primary key,
    job_id       text        not null,
    signed_at    timestamptz not null,
    first_run_at timestamptz not null default now()
);

create index if not exists ix_job_nonces_signed_at on platform.job_nonces (signed_at);

revoke all on platform.job_nonces from public;
revoke all on platform.job_nonces from erp_app;
grant select, insert, delete on platform.job_nonces to erp_worker;

alter table platform.job_nonces enable row level security;
alter table platform.job_nonces force row level security;
drop policy if exists job_nonces_worker on platform.job_nonces;
create policy job_nonces_worker on platform.job_nonces
    for all
    to erp_worker
    using (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null)
    with check (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null);

comment on table platform.job_nonces is
    'W-42: the job id each signed Hangfire job''s nonce first ran as (JobReplayLedger). erp_worker only, without any context; erp_app has no right on it. Jobs migration 0001.';
