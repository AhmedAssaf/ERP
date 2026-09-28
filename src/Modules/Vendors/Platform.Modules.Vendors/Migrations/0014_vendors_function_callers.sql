-- ADR-0012 point 4: security-definer functions state who may call them (pentest of the vendor slice, P-2, P-3, P-6).
--
-- P-2: vendor.approve_relationship (0003) checked the tenant and that an acting user exists, so a vendor session on the
-- tenant host could approve itself or a competitor. Approving is for tenant staff: it now refuses a vendor context, and
-- an acting user who is a vendor user (vendor sessions set the vendor context only after the Vendor policy, so a vendor's
-- own sub is refused too).
--
-- P-3 and P-6: the worker's functions (the upload cleanup's stale_uploads, claim_stale_upload and remove_stale_upload of
-- 0008, and the retry scan's pending_scan_documents of 0006) fixed the age and the queue but not the caller, so any web
-- session could list every company's uploads and pending documents, or remove another company's upload. The worker's
-- jobs run with neither a tenant nor a vendor context; a session with either is a web session. The two listing functions
-- then answer no rows (like the staff functions of 0011); the two that lock or delete a row refuse with 42501.
-- Same signatures, so CREATE OR REPLACE keeps the grants; they are restated anyway. No new table.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

create or replace function vendor.approve_relationship(p_company_id uuid)
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_tenant uuid := platform.current_tenant();
    v_user   text := platform.current_user_id();
begin
    if v_tenant is null or v_user is null then
        raise exception 'A vendor is approved by a signed-in user of the tenant.' using errcode = 'insufficient_privilege';
    end if;

    if platform.current_vendor_company() is not null
       or exists (select 1 from vendor.vendor_users u where u.user_id = v_user) then
        raise exception 'A vendor is approved by tenant staff, never in a vendor''s session.' using errcode = 'insufficient_privilege';
    end if;

    update vendor.relationships
    set status = 'approved', approved_by = v_user
    where tenant_id = v_tenant and company_id = p_company_id and status = 'pending';
    return found;
end
$$;

create or replace function vendor.stale_uploads()
    returns table (id uuid, chunk_count integer, company_id uuid, outcome text)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select u.id, u.chunk_count, u.company_id, u.outcome from vendor.uploads u
    where u.created_at < now() - interval '25 hours'
      and platform.current_tenant() is null
      and platform.current_vendor_company() is null
    order by u.created_at
    limit 500
$$;

create or replace function vendor.claim_stale_upload(p_upload_id uuid)
    returns table (id uuid, chunk_count integer, company_id uuid, outcome text)
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null then
        raise exception 'Only the worker cleans up uploads.' using errcode = 'insufficient_privilege';
    end if;

    return query
        select u.id, u.chunk_count, u.company_id, u.outcome from vendor.uploads u
        where u.id = p_upload_id and u.created_at < now() - interval '25 hours'
        for update skip locked;
end
$$;

create or replace function vendor.remove_stale_upload(p_upload_id uuid)
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    if platform.current_tenant() is not null or platform.current_vendor_company() is not null then
        raise exception 'Only the worker cleans up uploads.' using errcode = 'insufficient_privilege';
    end if;

    delete from vendor.uploads u where u.id = p_upload_id and u.created_at < now() - interval '25 hours';
    return found;
end
$$;

create or replace function vendor.pending_scan_documents(p_limit integer)
    returns table (id uuid, company_id uuid)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, d.company_id from vendor.documents d
    where d.scan_status = 'pending_scan' and d.scan_attempts < 12
      and platform.current_tenant() is null
      and platform.current_vendor_company() is null
    order by d.last_scan_at nulls first, d.created_at
    limit greatest(least(p_limit, 1000), 0)
$$;

revoke all on function vendor.approve_relationship(uuid) from public;
revoke all on function vendor.stale_uploads() from public;
revoke all on function vendor.claim_stale_upload(uuid) from public;
revoke all on function vendor.remove_stale_upload(uuid) from public;
revoke all on function vendor.pending_scan_documents(integer) from public;
grant execute on function vendor.approve_relationship(uuid) to erp_app;
grant execute on function vendor.stale_uploads() to erp_app;
grant execute on function vendor.claim_stale_upload(uuid) to erp_app;
grant execute on function vendor.remove_stale_upload(uuid) to erp_app;
grant execute on function vendor.pending_scan_documents(integer) to erp_app;
