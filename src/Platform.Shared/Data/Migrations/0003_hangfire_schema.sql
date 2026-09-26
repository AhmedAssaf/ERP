-- Hangfire storage (W-08, spec D-6). The schema is owned by the migration owner; the app role may create objects in
-- it and nothing else, so Hangfire.PostgreSql's PrepareSchemaIfNecessary builds and upgrades its own tables as erp_app.
-- Its install script creates the schema only when it is missing, so the database-level CREATE right is not needed.
-- Hangfire tables carry no tenant_id: jobs are platform infrastructure; the tenant travels as a job parameter.
create schema if not exists hangfire;
grant usage, create on schema hangfire to erp_app;
