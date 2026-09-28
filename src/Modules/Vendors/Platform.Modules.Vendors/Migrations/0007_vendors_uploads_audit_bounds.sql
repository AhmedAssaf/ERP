-- Vendor uploads: audit before commit, refused and daily upload bounds, orphan cleanup (second review of vendor plan
-- task 3; F-12, V-9, V-10). Additive: no table is created, so the row-level security of 0001 and 0005 covers every
-- column added here.
-- 1. An upload whose file is refused (type, empty, size) records outcome 'refused', so completing again answers the same
--    and it no longer counts as open. An expiry refusal stays open: the vendor corrects the date on the same upload.
-- 2. last_chunk_at: when the upload's latest chunk arrived. The open-upload bound counts only uploads with a start or a
--    chunk in the last hour, so an abandoned upload stops holding a place long before the cleanup removes it.
-- 3. An upload's document reference is checked at commit (deferrable), so the upload row and its document can be
--    written in either order inside one transaction.
-- 4. vendor.stale_uploads also returns the company and the outcome: for an upload that never recorded an outcome, the
--    cleanup job deletes the files a failed completion may have stored under the upload's id (document or quarantine).
-- 5. vendor.unpark_document(id): a platform operator gives a parked document (12 retry scans without a verdict) back to
--    the retry job. Owner only: neither erp_app nor public may execute it (docs/07 section 4).

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

-- 1. The 'refused' outcome. Same shapes as 0005 otherwise; a refused upload names no document, as an infected one.
alter table vendor.uploads
    drop constraint ck_uploads_outcome,
    drop constraint ck_uploads_outcome_shape;
alter table vendor.uploads
    add constraint ck_uploads_outcome check (outcome in ('clean', 'pending_scan', 'infected', 'refused')),
    add constraint ck_uploads_outcome_shape check (
        (outcome is null and document_id is null and sha256 is null)
        or (outcome in ('clean', 'pending_scan') and document_id is not null and sha256 ~ '^[a-f0-9]{64}$')
        or (outcome in ('infected', 'refused') and document_id is null));

-- 2. The latest chunk's time, set by the chunk statement at the database's clock.
alter table vendor.uploads add column last_chunk_at timestamptz null;
grant update (last_chunk_at) on vendor.uploads to erp_app;
-- The start bounds count a company's uploads of the last day.
create index ix_uploads_company_created_at on vendor.uploads (company_id, created_at);

-- 3. Checked at commit.
alter table vendor.uploads drop constraint fk_uploads_document;
alter table vendor.uploads
    add constraint fk_uploads_document foreign key (document_id, company_id) references vendor.documents (id, company_id)
        deferrable initially deferred;

-- 4. A new return type needs a new function; the grants of 0005 go with the old one and are given again.
drop function vendor.stale_uploads();
create function vendor.stale_uploads()
    returns table (id uuid, chunk_count integer, company_id uuid, outcome text)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select u.id, u.chunk_count, u.company_id, u.outcome from vendor.uploads u
    where u.created_at < now() - interval '24 hours'
    order by u.created_at
    limit 500
$$;
revoke all on function vendor.stale_uploads() from public;
grant execute on function vendor.stale_uploads() to erp_app;

-- 5. Unpark a parked document: attempts back to zero, so vendor.pending_scan_documents lists it again. Returns false
--    when the document is not pending or not parked.
create function vendor.unpark_document(p_document_id uuid)
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    update vendor.documents d set scan_attempts = 0, last_scan_at = null
    where d.id = p_document_id and d.scan_status = 'pending_scan' and d.scan_attempts >= 12;
    return found;
end
$$;
revoke all on function vendor.unpark_document(uuid) from public;
