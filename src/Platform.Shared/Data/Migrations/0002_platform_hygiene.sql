-- Only the owner running migrations may enable RLS on a table; functions are executable by PUBLIC by default.
revoke execute on function platform.enable_tenant_rls(text, text) from public;
