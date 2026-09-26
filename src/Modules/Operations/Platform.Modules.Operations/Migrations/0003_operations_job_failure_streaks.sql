-- F-60 "a job fails three times in a row": consecutive failures per job, counted per recurring job id when the job
-- has one (every run of a recurring job such as "health-check" is a new Hangfire job id, so a per-id count would never
-- reach three), and per job id otherwise. A success deletes the row (the streak resets); "alerted" makes the alert
-- fire once per streak. Platform-level like the rest of the ops schema: no tenant_id, no RLS, guarded by grants only
-- (spec section 6). A new script, since applied migrations are checksummed (see SqlMigrator).
create table ops.job_failure_streaks (
    job_key    text        primary key,
    failures   integer     not null,
    alerted    boolean     not null default false,
    updated_at timestamptz not null
);

grant select, insert, update, delete on ops.job_failure_streaks to erp_app;
