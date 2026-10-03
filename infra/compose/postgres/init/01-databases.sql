-- Runs once on first start of the postgres volume.
-- The platform database is created by POSTGRES_DB; Keycloak gets its own database on the same server.
CREATE DATABASE keycloak;

\connect platform
CREATE EXTENSION IF NOT EXISTS vector;      -- pgvector, for AI retrieval over offer chunks (F-46, F-47)
CREATE EXTENSION IF NOT EXISTS pgcrypto;    -- gen_random_uuid and hashing helpers
CREATE EXTENSION IF NOT EXISTS unaccent;    -- accent-insensitive search on Latin text

-- Application role used by the web host. It is NOT a superuser, so row-level security applies to it. The worker connects
-- as its own role erp_worker (a member of erp_app), which platform migration 0008 creates and the migrator gives a login
-- (W-36). The migration job runs as the owner (erp) and creates the RLS policies; the web host connects as erp_app.
CREATE ROLE erp_app LOGIN PASSWORD 'erp_app_dev_password';
GRANT CONNECT ON DATABASE platform TO erp_app;
GRANT USAGE ON SCHEMA public TO erp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO erp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO erp_app;
