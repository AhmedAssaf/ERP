-- Vendor scan queue and upload cleanup (third review of vendor plan task 3; F-12, V-9, V-10). Additive: no table is
-- created, so the row-level security of 0001 and 0005 covers everything here.
-- 1. The cleanup waits 25 hours, not 24: an upload is usable for a day, and a completion that began just before the
--    day ended may still be scanning and storing its file. Same signatures, definer, search_path and grants.
-- 2. vendor.claim_stale_upload(id) locks a stale upload's row for the caller's transaction (FOR UPDATE SKIP LOCKED) and
--    returns it, or nothing while a completion holds it. The cleanup job deletes objects only for a claimed row.
-- 3. vendor.unpark_document writes the platform audit (vendor.document_unparked, actor the operator's session role, data
--    the document id). The function's owner is the role that runs every module's migrations, which owns
--    ops.platform_audit; the audit row and the reset commit together.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

-- 1. The 25 hour margin.
create or replace function vendor.stale_uploads()
    returns table (id uuid, chunk_count integer, company_id uuid, outcome text)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select u.id, u.chunk_count, u.company_id, u.outcome from vendor.uploads u
    where u.created_at < now() - interval '25 hours'
    order by u.created_at
    limit 500
$$;

create or replace function vendor.remove_stale_upload(p_upload_id uuid)
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    delete from vendor.uploads u where u.id = p_upload_id and u.created_at < now() - interval '25 hours';
    return found;
end
$$;

-- 2. Claim a stale upload for the caller's transaction; a row a completion holds is skipped, not waited for.
create function vendor.claim_stale_upload(p_upload_id uuid)
    returns table (id uuid, chunk_count integer, company_id uuid, outcome text)
    language sql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
    select u.id, u.chunk_count, u.company_id, u.outcome from vendor.uploads u
    where u.id = p_upload_id and u.created_at < now() - interval '25 hours'
    for update skip locked
$$;

revoke all on function vendor.stale_uploads() from public;
revoke all on function vendor.remove_stale_upload(uuid) from public;
revoke all on function vendor.claim_stale_upload(uuid) from public;
grant execute on function vendor.stale_uploads() to erp_app;
grant execute on function vendor.remove_stale_upload(uuid) to erp_app;
grant execute on function vendor.claim_stale_upload(uuid) to erp_app;

-- 3. Unparking is audited in the platform audit, in the same transaction as the reset.
create or replace function vendor.unpark_document(p_document_id uuid)
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
declare
    v_company_id uuid;
begin
    update vendor.documents d set scan_attempts = 0, last_scan_at = null
    where d.id = p_document_id and d.scan_status = 'pending_scan' and d.scan_attempts >= 12
    returning d.company_id into v_company_id;
    if not found then
        return false;
    end if;

    insert into ops.platform_audit (id, actor_id, action, subject_type, subject_id, data)
    values (gen_random_uuid(), session_user::text, 'vendor.document_unparked', 'vendor_company', v_company_id::text,
            jsonb_build_object('document_id', p_document_id::text));
    return true;
end
$$;
revoke all on function vendor.unpark_document(uuid) from public;
