-- W-33 third review of PR #5 (2026-09-30), pentest P-R4: an upheld CR dispute's identity provider update (and its retry)
-- removed a removed user from a tenant's Keycloak organization even when the person was that tenant's staff by then,
-- undoing the tenant's decision and ending the staff sessions through W-21. The Vendors module now asks, through the
-- Identity module's public contract (IStaffTenancies), which tenants the user is staff of, and skips those
-- organizations. identity.members sits under forced tenant row-level security, and the platform console has no tenant,
-- so the answer comes from this security-definer function, which reads the rows as its owner.
--
-- A member counts from the invitation on (invited or active): StaffService adds the person to the tenant's organization
-- when it invites them, so that membership is already the tenant's decision.
--
-- Caller check (ADR-0012 point 4): a platform console session only (no tenant, no vendor company, an acting user);
-- any other session is refused with insufficient_privilege. plpgsql, so the body is checked when it runs, not when it is
-- created. No new table, so no new row-level security policy. Executable by erp_app only.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the identity migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create function identity.staff_tenants_of(p_user_id text)
    returns table (tenant_id uuid)
    language plpgsql
    stable
    security definer
    set search_path = identity, pg_temp
as $$
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null or platform.current_user_id() is null then
        raise exception 'The tenants a user is staff of are read in the platform console only.' using errcode = 'insufficient_privilege';
    end if;

    return query
        select distinct m.tenant_id
        from identity.members m
        where m.user_id = p_user_id and m.status in ('invited', 'active')
        order by m.tenant_id;
end
$$;

revoke all on function identity.staff_tenants_of(text) from public;
grant execute on function identity.staff_tenants_of(text) to erp_app;
