-- W-33 after merging W-21 (2026-09-30). W-21's /vendor/join refuses to put a related company's user back into a tenant's
-- organization ("your access was removed", pentest P-1). An upheld dispute adds the claimant to every related tenant's
-- organization itself; when that Keycloak step failed and waits for the platform admin's retry, the claimant who opens
-- /vendor/join meanwhile would read an untrue "your access was removed". This function lets the join tell the two apart:
-- true when the acting user, in their own company's vendor context, is the claimant of an upheld dispute of that company
-- whose identity provider update has not succeeded yet. It reveals nothing else, and answers false in any other session.
-- No new table, so no new row-level security policy.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create function vendor.claimant_awaiting_identity_provider()
    returns boolean
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select exists (
        select 1 from vendor.cr_disputes d
        where d.status = 'upheld'
          and d.idp_outcome is distinct from 'updated'
          and d.claimant_user_id = platform.current_user_id()
          and d.company_id = platform.current_vendor_company()
          and platform.current_user_id() is not null
          and platform.current_vendor_company() is not null)
$$;

revoke all on function vendor.claimant_awaiting_identity_provider() from public;
grant execute on function vendor.claimant_awaiting_identity_provider() to erp_app;
