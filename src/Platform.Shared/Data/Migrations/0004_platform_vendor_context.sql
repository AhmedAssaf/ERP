-- The vendor company of the current connection (vendor spec section 2, ADR-0008), or NULL when none is set. The
-- connection interceptor sets app.vendor_company_id beside app.tenant_id; the same NULLIF rule turns the empty string
-- of "no vendor" into NULL, so a connection without a vendor company sees zero vendor rows.
create or replace function platform.current_vendor_company() returns uuid
    language sql
    stable
as $$ select nullif(current_setting('app.vendor_company_id', true), '')::uuid $$;

grant execute on function platform.current_vendor_company() to erp_app;
