-- W-36 (ADR-0012 addendum 2026-10-03, pentest I-2): the worker's own role, erp_worker.
--
-- Until now the worker connected as erp_app, so "no tenant, no vendor and no acting user" was the only mark of a
-- worker-only function, and a platform console session matched it as well. The module migrations that follow this one
-- (tenancy 0010, identity 0004, operations 0007, vendors 0028) move each worker-only right from erp_app to erp_worker.
--
-- 1. The role. Created without a login, like erp_key_ring (0007); the migrator gives it one from ConnectionStrings:Worker
--    (WorkerRole, N-10: the password is never in a script). It is a member of erp_app with INHERIT and without SET: the
--    worker runs every module's jobs, tenant-scoped ones included, so it needs what erp_app has, plus its own rights, and
--    it never switches to erp_app. erp_app is not a member of erp_worker. CREATEROLE (or superuser) is needed to create
--    the role, and admin rights on erp_app to grant the membership (skipped when a pre-provisioned role already has it);
--    the Compose owner erp is a superuser.
-- 2. Hangfire's tables leave erp_app. They were created by Hangfire.PostgreSql at a host's first start as erp_app (0003),
--    so the application role could alter, drop or truncate them. From now on the migrator installs and upgrades them as
--    the owner (MigrationRunner, after the platform migrations) and the hosts never prepare the schema. erp_app loses
--    CREATE on the schema and ownership of every existing table (moved to the role running this migration, which must be
--    a member of erp_app for that on an existing database, or the superuser); it keeps USAGE and SELECT, INSERT, UPDATE,
--    DELETE on the tables and USAGE, SELECT on the sequences, which the web host needs to enqueue jobs, re-run a failed
--    one from the console and show the dashboard. Default privileges give tables a later Hangfire version adds the same.
--    erp_worker has these through erp_app.

-- A role an administrator created beforehand (docs/07, the pilot) is accepted only as this migration would create it: no
-- superuser, BYPASSRLS, CREATEROLE, CREATEDB or REPLICATION attribute, no membership but erp_app (inherit, no set), and
-- no member but the migration owner's own ADMIN OPTION. The membership is granted only when it is missing, so a
-- pre-provisioned role that has it needs no admin rights on erp_app here. Object grants an administrator gave such a
-- role directly are not checked here (docs/07).
do $$
declare
    v_worker  pg_roles%rowtype;
    v_app_oid oid := (select oid from pg_roles where rolname = 'erp_app');
begin
    select * into v_worker from pg_roles where rolname = 'erp_worker';
    if not found then
        create role erp_worker nologin inherit;
        select * into v_worker from pg_roles where rolname = 'erp_worker';
    end if;

    if v_worker.rolsuper or v_worker.rolbypassrls or v_worker.rolcreaterole or v_worker.rolcreatedb or v_worker.rolreplication then
        raise exception 'Role erp_worker exists with superuser, BYPASSRLS, CREATEROLE, CREATEDB or REPLICATION; remove the attribute before migrating.';
    end if;

    if exists (select 1 from pg_auth_members m where m.member = v_worker.oid and m.roleid <> v_app_oid) then
        raise exception 'Role erp_worker is a member of a role other than erp_app; revoke that membership before migrating.';
    end if;

    -- A CREATEROLE owner holds ADMIN OPTION on a role it created (PostgreSQL 16), without INHERIT or SET. That row is
    -- accepted for the role running this migration only; any other member of erp_worker is refused.
    if exists (select 1 from pg_auth_members m
               where m.roleid = v_worker.oid
                 and not (m.member = (select oid from pg_roles where rolname = current_user)
                          and m.admin_option and not m.inherit_option and not m.set_option)) then
        raise exception 'Another role is a member of erp_worker (only the migration owner''s ADMIN OPTION without INHERIT or SET is accepted); revoke that membership before migrating.';
    end if;

    if exists (select 1 from pg_auth_members m
               where m.member = v_worker.oid and m.roleid = v_app_oid and (not m.inherit_option or m.set_option)) then
        raise exception 'Role erp_worker is a member of erp_app without INHERIT or with SET; grant it with inherit true, set false.';
    end if;

    if not exists (select 1 from pg_auth_members m where m.member = v_worker.oid and m.roleid = v_app_oid) then
        grant erp_app to erp_worker with inherit true, set false;
    end if;
end
$$;

do $$
begin
    execute format('grant connect on database %I to erp_worker', current_database());
end
$$;

revoke create on schema hangfire from erp_app;
grant usage on schema hangfire to erp_app;

do $$
declare
    v_object record;
begin
    -- Tables first: their serial sequences follow them. Then any sequence still owned by another role and not linked to a
    -- table column.
    for v_object in
        select c.relname
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        where n.nspname = 'hangfire' and c.relkind in ('r', 'p') and c.relowner <> (select oid from pg_roles where rolname = current_user)
    loop
        execute format('alter table hangfire.%I owner to %I', v_object.relname, current_user);
    end loop;

    for v_object in
        select c.relname
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        where n.nspname = 'hangfire' and c.relkind = 'S' and c.relowner <> (select oid from pg_roles where rolname = current_user)
          and not exists (select 1 from pg_depend d where d.classid = 'pg_class'::regclass and d.objid = c.oid and d.deptype in ('a', 'i'))
    loop
        execute format('alter sequence hangfire.%I owner to %I', v_object.relname, current_user);
    end loop;
end
$$;

grant select, insert, update, delete on all tables in schema hangfire to erp_app;
grant usage, select on all sequences in schema hangfire to erp_app;
alter default privileges in schema hangfire grant select, insert, update, delete on tables to erp_app;
alter default privileges in schema hangfire grant usage, select on sequences to erp_app;

comment on role erp_worker is
    'Platform.Worker (W-36): member of erp_app (inherit, no set) plus the worker-only rights; login from ConnectionStrings:Worker.';
