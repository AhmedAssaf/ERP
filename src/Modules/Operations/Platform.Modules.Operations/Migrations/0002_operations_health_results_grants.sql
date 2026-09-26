-- ops.health_results is insert-and-select only for erp_app (spec section 6): a health-check result is a fact
-- recorded once per cycle, never edited or removed by the application role. 0001 granted select, insert, update,
-- delete; this narrows it. Migrations are checksummed and never edited after being applied (see SqlMigrator), so
-- this is a new script rather than a change to 0001.
revoke update, delete on ops.health_results from erp_app;
