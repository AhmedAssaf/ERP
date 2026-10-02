-- W-36 (ADR-0012 addendum 2026-10-03, pentest I-2, PT-W10-01): the worker's writes in ops become rights of the worker's own
-- role (platform 0008), and the two tables the worker writes without row-level security get it.
--
-- Who does what:
-- - The worker (erp_worker) records health results, opens, updates and closes incidents (health-check job, F-51, F-60),
--   keeps the job failure streaks (JobFailureAlertFilter, F-60) and replaces the active-user counts (usage job, W-10).
--   It always runs these with no tenant, vendor or user context.
-- - The platform console (erp_app, no tenant or vendor context, with an acting user) reads the board, the incidents and
--   the usage counts, and nothing else here. A tenant or vendor session reads none of the incidents.
--
-- 1. ops.health_results: erp_app keeps SELECT and loses INSERT (no row-level security, unchanged: grants only).
-- 2. ops.incidents: erp_app keeps SELECT and loses INSERT, UPDATE and DELETE; erp_worker gets INSERT and UPDATE (the
--    worker never deletes one). Forced row-level security: reading needs neither a tenant nor a vendor context (the
--    console and the worker, as ops.platform_audit, 0004); inserting and updating are for erp_worker without any context.
-- 3. ops.job_failure_streaks: erp_app loses every right; erp_worker gets SELECT, INSERT, UPDATE and DELETE. Forced
--    row-level security: erp_worker without any context only.
-- 4. ops.active_user_counts (0006): erp_app keeps SELECT and loses INSERT and DELETE, which go to erp_worker; the two write
--    policies now name erp_worker and keep their context rule.
-- erp_worker reads through its membership of erp_app (SELECT and the read policies, which apply to every role).

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the operations migration as a role with BYPASSRLS (or the superuser): its security-definer functions write across row-level security as their owner.';
    end if;

    if not exists (select 1 from pg_roles where rolname = 'erp_worker') then
        raise exception 'Run the platform migrations first: platform 0008 creates erp_worker.';
    end if;
end
$$;

-- 1. Health results.
revoke insert, update, delete on ops.health_results from erp_app;
grant insert on ops.health_results to erp_worker;

-- 2. Incidents.
revoke insert, update, delete on ops.incidents from erp_app;
grant select on ops.incidents to erp_app;
grant insert, update on ops.incidents to erp_worker;

alter table ops.incidents enable row level security;
alter table ops.incidents force row level security;
drop policy if exists incidents_read on ops.incidents;
drop policy if exists incidents_worker_insert on ops.incidents;
drop policy if exists incidents_worker_update on ops.incidents;
create policy incidents_read on ops.incidents
    for select
    using (platform.current_tenant() is null and platform.current_vendor_company() is null);
create policy incidents_worker_insert on ops.incidents
    for insert
    to erp_worker
    with check (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null);
create policy incidents_worker_update on ops.incidents
    for update
    to erp_worker
    using (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null)
    with check (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null);

-- 3. Job failure streaks.
revoke all on ops.job_failure_streaks from erp_app;
grant select, insert, update, delete on ops.job_failure_streaks to erp_worker;

alter table ops.job_failure_streaks enable row level security;
alter table ops.job_failure_streaks force row level security;
drop policy if exists job_failure_streaks_worker on ops.job_failure_streaks;
create policy job_failure_streaks_worker on ops.job_failure_streaks
    for all
    to erp_worker
    using (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null)
    with check (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null);

-- 4. Active-user counts.
revoke insert, update, delete on ops.active_user_counts from erp_app;
grant insert, delete on ops.active_user_counts to erp_worker;

drop policy if exists worker_insert on ops.active_user_counts;
drop policy if exists worker_delete on ops.active_user_counts;
create policy worker_insert on ops.active_user_counts
    for insert
    to erp_worker
    with check (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null);
create policy worker_delete on ops.active_user_counts
    for delete
    to erp_worker
    using (platform.current_tenant() is null and platform.current_vendor_company() is null and platform.current_user_id() is null);

comment on table ops.incidents is
    'F-51, F-60 incidents (Operations module). Written by erp_worker only, without any context; read without a tenant or vendor context (the platform console and the worker). Operations migration 0007 (W-36).';
comment on table ops.job_failure_streaks is
    'F-60 consecutive job failures (Operations module). erp_worker only, without any context; erp_app has no right on it. Operations migration 0007 (W-36).';
