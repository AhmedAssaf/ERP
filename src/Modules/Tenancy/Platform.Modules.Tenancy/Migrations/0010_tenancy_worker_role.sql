-- W-36 (ADR-0012 addendum 2026-10-03): tenancy.referenced_logos (0009) is the logo cleanup job's, so only the worker's own
-- role (platform 0008) may execute it; erp_app, and with it every platform console session, may not. The function keeps
-- its context rule as defence in depth. No new table, so no new row-level security policy.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the tenancy migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;

    if not exists (select 1 from pg_roles where rolname = 'erp_worker') then
        raise exception 'Run the platform migrations first: platform 0008 creates erp_worker.';
    end if;
end
$$;

revoke all on function tenancy.referenced_logos() from public;
revoke all on function tenancy.referenced_logos() from erp_app;
grant execute on function tenancy.referenced_logos() to erp_worker;
