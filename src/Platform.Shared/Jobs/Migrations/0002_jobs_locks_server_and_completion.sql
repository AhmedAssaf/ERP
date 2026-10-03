-- W-42 fix round 1 (review 2026-10-03): three more rows of Hangfire's tables that erp_app could use against the worker,
-- and completed jobs in the replay ledger.
--
-- 1. hangfire.lock: a state lock erp_app writes must carry an acquisition time, at most five minutes ahead. Hangfire.PostgreSql
--    removes a stale lock only by "acquired < timeout", so a row with no acquisition time (allowed by 0001) never expired and
--    could hold the state of a future job id, every job included, for good. The column is timestamptz, so now() compares
--    as it should whatever the session's time zone.
-- 2. hangfire.job: erp_app may insert or move a job only to an id the job sequence has already handed out. An explicit id
--    ahead of the sequence would collide with a later job the web host or the worker creates, so that enqueue would fail.
--    Ids are compared with the sequence's last value, which never trails an id it handed out; erp_worker and the owner are
--    unrestricted, and erp_app still reads every row (the dashboard, the console's failed jobs).
-- 3. hangfire.server: erp_app keeps SELECT (the dashboard) and loses INSERT, UPDATE and DELETE. The web host runs no job
--    server, and a heartbeat row written by the application role could keep the worker's heartbeat check green while the
--    worker is down (F-51, F-60). erp_worker gets the rights directly, as it no longer has them through erp_app.
-- 4. platform.job_nonces.completed_at: set by the worker when a job ran without an exception (JobReplayLedger.Complete); a
--    completed nonce never admits a run again, so a succeeded job moved back to the queue by erp_app is refused. A failed
--    job's retries and the console's re-run still run.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the jobs migrations as a role with BYPASSRLS (or the superuser): Hangfire''s install and upgrades run as the owner under forced row-level security.';
    end if;

    if pg_get_serial_sequence('hangfire.job', 'id') is distinct from 'hangfire.job_id_seq' then
        raise exception 'hangfire.job.id is expected to take its values from hangfire.job_id_seq; check the Hangfire.PostgreSql upgrade before migrating.';
    end if;
end
$$;

-- 1. Locks.
drop policy if exists lock_app_insert on hangfire.lock;
drop policy if exists lock_app_update on hangfire.lock;
create policy lock_app_insert on hangfire.lock
    for insert
    to erp_app
    with check (resource like 'hangfire:job:%:state-lock' and acquired is not null and acquired <= now() + interval '5 minutes');
create policy lock_app_update on hangfire.lock
    for update
    to erp_app
    using (resource like 'hangfire:job:%:state-lock')
    with check (resource like 'hangfire:job:%:state-lock' and acquired is not null and acquired <= now() + interval '5 minutes');

-- 2. Job ids.
alter table hangfire.job enable row level security;
alter table hangfire.job force row level security;
drop policy if exists job_read on hangfire.job;
drop policy if exists job_app_insert on hangfire.job;
drop policy if exists job_app_update on hangfire.job;
drop policy if exists job_app_delete on hangfire.job;
drop policy if exists job_worker on hangfire.job;
create policy job_read on hangfire.job
    for select
    to erp_app
    using (true);
create policy job_app_insert on hangfire.job
    for insert
    to erp_app
    with check (id <= (select last_value from hangfire.job_id_seq));
create policy job_app_update on hangfire.job
    for update
    to erp_app
    using (true)
    with check (id <= (select last_value from hangfire.job_id_seq));
create policy job_app_delete on hangfire.job
    for delete
    to erp_app
    using (true);
create policy job_worker on hangfire.job
    for all
    to erp_worker
    using (true)
    with check (true);

-- 3. Servers.
revoke insert, update, delete on hangfire.server from erp_app;
grant select on hangfire.server to erp_app;
grant select, insert, update, delete on hangfire.server to erp_worker;

-- 4. Completed jobs.
alter table platform.job_nonces add column if not exists completed_at timestamptz;
grant update (completed_at) on platform.job_nonces to erp_worker;

comment on table platform.job_nonces is
    'W-42: the job id each signed Hangfire job''s nonce first ran as, and when that job succeeded (JobReplayLedger). erp_worker only, without any context; erp_app has no right on it. Jobs migrations 0001 and 0002.';
