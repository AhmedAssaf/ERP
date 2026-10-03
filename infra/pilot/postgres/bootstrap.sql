-- W-19: what infra/compose/postgres/init/01-databases.sql does in development, for the pilot, and safe to repeat:
-- deploy.sh runs it on every deploy as the owner (POSTGRES_USER, the image's superuser) over the local socket.
-- Passwords never appear here or on a command line: deploy.sh computes each role's SCRAM-SHA-256 verifier on the VM
-- (lib.sh, scram_verifier) and passes it in the environment; PostgreSQL stores the verifier and never sees the password.
-- erp_key_ring (W-24) and erp_worker (W-36) are created by platform migrations 0007 and 0008 and get their logins from the
-- migrator, which also sends only verifiers.
\set ON_ERROR_STOP on
\getenv app_verifier ERP_APP_DB_VERIFIER
\getenv keycloak_verifier KEYCLOAK_DB_VERIFIER
-- psql's \quit takes no exit code, so a missing value raises an error instead (ON_ERROR_STOP exits non-zero).
\if :{?app_verifier}
\else
  do $$ begin raise exception 'ERP_APP_DB_VERIFIER is not set'; end $$;
\endif
\if :{?keycloak_verifier}
\else
  do $$ begin raise exception 'KEYCLOAK_DB_VERIFIER is not set'; end $$;
\endif

-- Application role of the web host. Never a superuser and never BYPASSRLS, so row-level security applies to it.
select 'create role erp_app login' where not exists (select from pg_roles where rolname = 'erp_app') \gexec
alter role erp_app with login nosuperuser nocreatedb nocreaterole noreplication nobypassrls password :'app_verifier';

-- Keycloak's own role and database: Keycloak never connects as the superuser on the pilot.
select 'create role keycloak login' where not exists (select from pg_roles where rolname = 'keycloak') \gexec
alter role keycloak with login nosuperuser nocreatedb nocreaterole noreplication nobypassrls password :'keycloak_verifier';
select 'create database keycloak owner keycloak template template0 encoding ''UTF8''' where not exists (select from pg_database where datname = 'keycloak') \gexec
alter database keycloak owner to keycloak;

-- Nobody connects to a database by default; each role is granted the one it needs (the migrations grant CONNECT on
-- platform to erp_key_ring and erp_worker themselves).
revoke connect, temporary on database platform from public;
revoke connect, temporary on database keycloak from public;
grant connect on database platform to erp_app;
grant connect on database keycloak to keycloak;

\connect platform
create extension if not exists vector;      -- pgvector, for AI retrieval over offer chunks (F-46, F-47)
create extension if not exists pgcrypto;    -- gen_random_uuid and hashing helpers
create extension if not exists unaccent;    -- accent-insensitive search on Latin text

grant usage on schema public to erp_app;
alter default privileges in schema public grant select, insert, update, delete on tables to erp_app;
alter default privileges in schema public grant usage, select on sequences to erp_app;

select format('bootstrap done: erp_app superuser=%s bypassrls=%s', rolsuper, rolbypassrls) from pg_roles where rolname = 'erp_app';
