-- W-33 review of 0022 (2026-09-30): vendor.claimant_awaiting_identity_provider() answered true while the dispute's overall
-- identity provider outcome was anything but "updated", so a claimant whose add to beta's organization had succeeded, but
-- whose dispute had another failed step (the squatter's role not taken back, say), and whom beta later removed, was told
-- "WaslaBid is still giving you access" on beta's /vendor/join instead of W-21's refusal, and the refusal went unaudited.
-- The check now looks at the host organization's own step only: true when the acting user, in their own company's vendor
-- context, is the claimant of an upheld dispute of that company whose "organization:add:<alias>" step for the given
-- organization failed or has no recorded outcome. Any other step does not matter here. No new table, so no new
-- row-level security policy; the function of 0022 goes, so there is one.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

drop function vendor.claimant_awaiting_identity_provider();

create function vendor.claimant_awaiting_organization(p_organization_alias text)
    returns boolean
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select exists (
        select 1 from vendor.cr_disputes d
        where d.status = 'upheld'
          and d.claimant_user_id = platform.current_user_id()
          and d.company_id = platform.current_vendor_company()
          and platform.current_user_id() is not null
          and platform.current_vendor_company() is not null
          and coalesce(d.idp_details ->> ('organization:add:' || p_organization_alias), '') <> 'done')
$$;

revoke all on function vendor.claimant_awaiting_organization(text) from public;
grant execute on function vendor.claimant_awaiting_organization(text) to erp_app;
