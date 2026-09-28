-- Review of the vendor slice (2026-09-28): owner guard. ops.write_platform_audit (0004) inserts into ops.platform_audit,
-- which is under forced row-level security with a select policy only, so it works only when its owner bypasses
-- row-level security. This module's migrations must therefore run as a role with BYPASSRLS (or the superuser), as the
-- vendors migrations always check. 0004 lacked the check and is applied and checksummed, so it is made here, for the
-- current role and for the owner of every security-definer function this module has. No schema change.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the operations migration as a role with BYPASSRLS (or the superuser): its security-definer functions write across row-level security as their owner.';
    end if;

    if exists (select 1
               from pg_proc p
               join pg_namespace n on n.oid = p.pronamespace
               join pg_roles r on r.oid = p.proowner
               where n.nspname = 'ops' and p.prosecdef and not (r.rolsuper or r.rolbypassrls)) then
        raise exception 'An ops security-definer function is owned by a role without BYPASSRLS; reassign it to the migration owner before migrating.';
    end if;
end
$$;
