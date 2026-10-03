-- W-36 (ADR-0012 addendum 2026-10-03, PT-W10-01): counting and pruning the active-user buckets (0003) are the usage job's,
-- so only the worker's own role (platform 0008) may execute identity.activity_counts and identity.prune_activity; erp_app,
-- and with it every platform console session, may not. Both keep their context rule as defence in depth. No new table.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the identity migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;

    if not exists (select 1 from pg_roles where rolname = 'erp_worker') then
        raise exception 'Run the platform migrations first: platform 0008 creates erp_worker.';
    end if;
end
$$;

revoke all on function identity.activity_counts(timestamptz) from public;
revoke all on function identity.prune_activity(timestamptz) from public;
revoke all on function identity.activity_counts(timestamptz) from erp_app;
revoke all on function identity.prune_activity(timestamptz) from erp_app;
grant execute on function identity.activity_counts(timestamptz) to erp_worker;
grant execute on function identity.prune_activity(timestamptz) to erp_worker;

comment on table identity.user_activity is
    'W-10 active users: hourly buckets per tenant, user (Keycloak sub) and kind, 35 days. erp_app inserts its own row only (identity migration 0003); counts and pruning only through identity.activity_counts and identity.prune_activity, which only erp_worker may execute (identity migration 0004, W-36).';
