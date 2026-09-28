-- The acting user of the current connection (vendor slice hardening), or NULL when none is set: the authenticated
-- principal's Keycloak sub. The connection interceptor sets app.user_id beside app.tenant_id and app.vendor_company_id
-- from the acting-user accessor, which only the host sets (middleware after authentication, the circuit handler). The
-- same NULLIF rule as the other two turns the empty string of "nobody" into NULL. Security-definer functions read it to
-- record who acted instead of trusting a parameter the caller chose.
create or replace function platform.current_user_id() returns text
    language sql
    stable
as $$ select nullif(current_setting('app.user_id', true), '') $$;

grant execute on function platform.current_user_id() to erp_app;
