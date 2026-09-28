-- Vendor document uploads (vendor plan task 3; F-12, V-8 to V-10, ADR-0001).
-- 1. vendor.uploads: the state of a chunked upload (owner company, document type, declared file, received chunks, and
--    its outcome once complete), under the company policy like every platform-level vendor table. The chunks themselves
--    are staged in object storage under staging/{upload id}/{index}.
-- 2. The application role never deletes an upload row. Abandoned uploads (older than a day) are found and removed by
--    the worker's cleanup job through two security-definer functions that refuse anything younger.
-- 3. A document found clean by the retry scan leaves quarantine: the application role may change its object key.
-- 4. The retry scan lists pending documents across companies through a security-definer function (ids only), then works
--    on each one under that company's own vendor context; erp_app never bypasses row-level security.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

-- 1. Upload state. The chunk count follows from the declared size and the chunk size; received_chunks holds the
--    distinct indexes that arrived. outcome, document_id and sha256 record a completion, so completing again answers
--    the same (clean or pending_scan with the document; infected without one).
create table vendor.uploads (
    id              uuid        primary key,
    company_id      uuid        not null references vendor.companies (id),
    document_type   text        not null,
    file_name       text        not null,
    content_type    text        not null,
    declared_size   bigint      not null,
    chunk_size      integer     not null,
    chunk_count     integer     not null,
    received_chunks integer[]   not null default '{}',
    outcome         text        null,
    document_id     uuid        null,
    sha256          text        null,
    created_at      timestamptz not null default now(),
    constraint ck_uploads_document_type check (document_type in ('cr_certificate', 'vat_certificate')),
    constraint ck_uploads_file_name check (char_length(file_name) between 1 and 255 and btrim(file_name) <> ''),
    constraint ck_uploads_content_type check (content_type in ('application/pdf', 'image/png', 'image/jpeg')),
    constraint ck_uploads_declared_size check (declared_size between 1 and 10485760),
    constraint ck_uploads_chunk_size check (chunk_size between 1 and 1048576),
    constraint ck_uploads_chunk_count check (chunk_count = (declared_size + chunk_size - 1) / chunk_size),
    constraint ck_uploads_outcome check (outcome in ('clean', 'pending_scan', 'infected')),
    constraint ck_uploads_outcome_shape check (
        (outcome is null and document_id is null and sha256 is null)
        or (outcome in ('clean', 'pending_scan') and document_id is not null and sha256 ~ '^[a-f0-9]{64}$')
        or (outcome = 'infected' and document_id is null))
);
create index ix_uploads_company on vendor.uploads (company_id);
create index ix_uploads_created_at on vendor.uploads (created_at);

alter table vendor.uploads enable row level security;
alter table vendor.uploads force row level security;
create policy vendor_isolation on vendor.uploads
    using (company_id = platform.current_vendor_company())
    with check (company_id = platform.current_vendor_company());

-- No DELETE: rows go only through vendor.remove_stale_upload below. What is declared at the start never changes.
grant select, insert on vendor.uploads to erp_app;
grant update (received_chunks, outcome, document_id, sha256) on vendor.uploads to erp_app;

-- 2. Cleanup of abandoned uploads, a day after they started. Both functions fix the age themselves, so no caller can
--    remove an upload that may still be in progress.
create function vendor.stale_uploads()
    returns table (id uuid, chunk_count integer)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select u.id, u.chunk_count from vendor.uploads u
    where u.created_at < now() - interval '24 hours'
    order by u.created_at
    limit 500
$$;

create function vendor.remove_stale_upload(p_upload_id uuid)
    returns boolean
    language plpgsql
    volatile
    security definer
    set search_path = vendor, pg_temp
as $$
begin
    delete from vendor.uploads u where u.id = p_upload_id and u.created_at < now() - interval '24 hours';
    return found;
end
$$;

-- 3. The retry scan moves a clean file out of quarantine.
grant update (object_key) on vendor.documents to erp_app;

-- 4. Documents waiting for their scan, oldest first: ids and companies only, never content or keys.
create function vendor.pending_scan_documents(p_limit integer)
    returns table (id uuid, company_id uuid)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, d.company_id from vendor.documents d
    where d.scan_status = 'pending_scan'
    order by d.created_at
    limit greatest(least(p_limit, 1000), 0)
$$;
create index ix_documents_pending_scan on vendor.documents (created_at) where scan_status = 'pending_scan';

revoke all on function vendor.stale_uploads() from public;
revoke all on function vendor.remove_stale_upload(uuid) from public;
revoke all on function vendor.pending_scan_documents(integer) from public;
grant execute on function vendor.stale_uploads() to erp_app;
grant execute on function vendor.remove_stale_upload(uuid) to erp_app;
grant execute on function vendor.pending_scan_documents(integer) to erp_app;
