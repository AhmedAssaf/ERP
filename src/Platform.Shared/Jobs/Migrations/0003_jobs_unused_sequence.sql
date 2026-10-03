-- W-42 fix round 2 (review m-2): the job id rule of 0002 compared with the sequence's last_value, which on a sequence that
-- has never handed out an id is its start value (1) with is_called false, so erp_app could take id 1 before the first job.
-- The highest id handed out is last_value when is_called, and one less otherwise.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the jobs migrations as a role with BYPASSRLS (or the superuser): Hangfire''s install and upgrades run as the owner under forced row-level security.';
    end if;
end
$$;

drop policy if exists job_app_insert on hangfire.job;
drop policy if exists job_app_update on hangfire.job;
create policy job_app_insert on hangfire.job
    for insert
    to erp_app
    with check (id <= (select last_value - (not is_called)::int from hangfire.job_id_seq));
create policy job_app_update on hangfire.job
    for update
    to erp_app
    using (true)
    with check (id <= (select last_value - (not is_called)::int from hangfire.job_id_seq));
