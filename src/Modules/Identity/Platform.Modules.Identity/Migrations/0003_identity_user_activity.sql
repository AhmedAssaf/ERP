-- W-10 business metrics, active users (spec docs/superpowers/specs/2026-09-30-observability-design.md section 6.4, O-21;
-- Q10 adopted as recommended on 2026-09-30): one hourly bucket per tenant, user and kind, written at most once an hour
-- by the web host (UserActivityMiddleware and the circuit handler) and counted by the worker's usage job.
--
-- Numbered 0003, not 0002: pull request #5 (W-33) adds identity migration 0002_identity_staff_tenants.sql. The two are
-- independent (a new table and functions here, one function there), and SqlMigrator applies every script not yet
-- journaled in name order, so either merge order works; on a database that has this script first, 0002 simply runs later.
--
-- Isolation (ADR-0012), modelled on audit.events (audit 0002 and 0003):
-- 1. Forced row-level security with two policies. tenant_isolation, SELECT only, with the staff-only rule of
--    platform.enable_tenant_rls (a second wall; erp_app has no SELECT grant, so no request-path session reads activity,
--    not even staff of the same tenant). tenant_activity_insert, INSERT only: a session writes only its own row
--    (user_id = the acting user), for its host tenant, as staff without a vendor context or as a vendor with one.
--    platform.enable_tenant_rls is not used, since its policy is for all commands.
-- 2. The database's clock: a trigger sets hour = date_trunc('hour', now()) on every insert, so no session chooses its
--    hour (a writer that names one is corrected, not refused: the hour is not evidence, only a bucket).
-- 3. erp_app holds INSERT only: no UPDATE or DELETE, and pruning goes through identity.prune_activity.
-- 4. Counting and pruning are security-definer functions that answer only a session with neither a tenant nor a vendor
--    context (the worker's, ADR-0012 point 4, as vendor.stale_uploads), refusing any other with 42501. They return
--    counts, never rows. Until a separate worker role exists, a platform console session also has no context (the known
--    gap pinned in VendorFunctionCallerTests); it serves only platform admins with OTP.
-- Retention: buckets older than 35 days are deleted by the usage job once a day (the 30-day window plus margin; Q7).

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the identity migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create table identity.user_activity (
    tenant_id uuid        not null,
    -- The Keycloak sub: pseudonymous, never an email or a name (spec 6.8).
    user_id   text        not null,
    kind      text        not null check (kind in ('staff', 'vendor')),
    hour      timestamptz not null,
    primary key (tenant_id, user_id, kind, hour)
);

-- The windows and the prune read by hour.
create index ix_user_activity_hour on identity.user_activity (hour);

alter table identity.user_activity enable row level security;
alter table identity.user_activity force row level security;

create policy tenant_isolation on identity.user_activity
    for select
    using (tenant_id = platform.current_tenant() and platform.current_vendor_company() is null);

create policy tenant_activity_insert on identity.user_activity
    for insert
    with check (
        tenant_id = platform.current_tenant()
        and user_id = platform.current_user_id()
        and ((kind = 'staff' and platform.current_vendor_company() is null)
             or (kind = 'vendor' and platform.current_vendor_company() is not null)));

create function identity.user_activity_database_hour()
    returns trigger
    language plpgsql
    set search_path = identity, pg_temp
as $$
begin
    new.hour := date_trunc('hour', now());
    return new;
end
$$;

revoke all on function identity.user_activity_database_hour() from public;

create trigger tr_user_activity_database_hour
    before insert on identity.user_activity
    for each row execute function identity.user_activity_database_hour();

grant insert on identity.user_activity to erp_app;

-- Distinct users per tenant, kind and window, plus the same across tenants (tenant_id null, each user once), for the
-- windows 1d, 7d and 30d: the last 24, 168 and 720 hourly buckets up to p_now, so a user seen at 09:10 counts in 1d until
-- about 09:00 the next day. Every tenant and kind with no bucket in a window is simply absent.
create function identity.activity_counts(p_now timestamptz)
    returns table (tenant_id uuid, kind text, time_window text, users integer)
    language plpgsql
    stable
    security definer
    set search_path = identity, pg_temp
as $$
#variable_conflict use_column
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null then
        raise exception 'Activity is counted by the worker only.' using errcode = 'insufficient_privilege';
    end if;

    return query
        select a.tenant_id, a.kind, w.time_window, count(distinct a.user_id)::integer
        from identity.user_activity a
        join (values ('1d', interval '24 hours'), ('7d', interval '7 days'), ('30d', interval '30 days')) as w(time_window, span)
            on a.hour > date_trunc('hour', p_now) - w.span and a.hour <= p_now
        group by grouping sets ((a.tenant_id, a.kind, w.time_window), (a.kind, w.time_window));
end
$$;

-- Deletes the buckets older than p_before; returns how many.
create function identity.prune_activity(p_before timestamptz)
    returns integer
    language plpgsql
    volatile
    security definer
    set search_path = identity, pg_temp
as $$
declare
    v_deleted integer;
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null then
        raise exception 'Activity is pruned by the worker only.' using errcode = 'insufficient_privilege';
    end if;

    delete from identity.user_activity where hour < p_before;
    get diagnostics v_deleted = row_count;
    return v_deleted;
end
$$;

revoke all on function identity.activity_counts(timestamptz) from public;
revoke all on function identity.prune_activity(timestamptz) from public;
grant execute on function identity.activity_counts(timestamptz) to erp_app;
grant execute on function identity.prune_activity(timestamptz) to erp_app;

comment on table identity.user_activity is
    'W-10 active users: hourly buckets per tenant, user (Keycloak sub) and kind, 35 days. erp_app inserts its own row only (identity migration 0003); counts and pruning only through identity.activity_counts and identity.prune_activity, for sessions without a tenant or vendor context.';
