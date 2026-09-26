-- The vendor company of a signed-in user (vendor plan task 2). The Vendor policy asks it before any vendor context is
-- set, and row-level security hides vendor.vendor_users until then (0001: company_id = platform.current_vendor_company()).
-- It answers one question about one user: the caller passes the principal's own sub, never form input. Like the
-- functions of 0001 it runs as its owner (the migration role, BYPASSRLS checked there) with a pinned search_path, and
-- only the application role may call it.
create function vendor.company_of_user(p_user_id text) returns uuid
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$ select u.company_id from vendor.vendor_users u where u.user_id = p_user_id $$;

revoke all on function vendor.company_of_user(text) from public;
grant execute on function vendor.company_of_user(text) to erp_app;
