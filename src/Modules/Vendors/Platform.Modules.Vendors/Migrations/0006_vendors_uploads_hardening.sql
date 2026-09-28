-- Vendor uploads hardening (review of vendor plan task 3; F-12, V-8 to V-10). Additive: no table is created, so the
-- row-level security of 0001 and 0005 covers every column added here.
-- 1. Retry scans are counted: scan_attempts and last_scan_at on vendor.documents. The retry job tries the documents
--    waited on longest first, and a document the scanner could not decide on 12 times is parked for a person: it stays
--    pending_scan (never listed, never current) but vendor.pending_scan_documents no longer returns it.
-- 2. A document's object key is its own: vendors/{company}/documents/{id} or vendors/{company}/quarantine/{id}, exactly as
--    VendorDocumentFiles builds them (uuid text is lower case with hyphens in both).
-- 3. A completed upload names a document of its own company only.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;
end
$$;

-- 1. Retry scan attempts.
alter table vendor.documents
    add column scan_attempts integer     not null default 0,
    add column last_scan_at  timestamptz null,
    add constraint ck_documents_scan_attempts check (scan_attempts >= 0);

grant update (scan_attempts, last_scan_at) on vendor.documents to erp_app;

-- Same signature as in 0005, so its grants stay; they are stated again below all the same.
create or replace function vendor.pending_scan_documents(p_limit integer)
    returns table (id uuid, company_id uuid)
    language sql
    stable
    security definer
    set search_path = vendor, pg_temp
as $$
    select d.id, d.company_id from vendor.documents d
    where d.scan_status = 'pending_scan' and d.scan_attempts < 12
    order by d.last_scan_at nulls first, d.created_at
    limit greatest(least(p_limit, 1000), 0)
$$;

drop index vendor.ix_documents_pending_scan;
create index ix_documents_pending_scan on vendor.documents (last_scan_at nulls first, created_at)
    where scan_status = 'pending_scan' and scan_attempts < 12;

revoke all on function vendor.pending_scan_documents(integer) from public;
grant execute on function vendor.pending_scan_documents(integer) to erp_app;

-- 2. Object keys.
alter table vendor.documents
    add constraint ck_documents_object_key check (
        object_key = 'vendors/' || company_id::text || '/documents/' || id::text
        or object_key = 'vendors/' || company_id::text || '/quarantine/' || id::text);

-- 3. An upload's document belongs to the upload's company. The pair needs a unique key to be referenced.
alter table vendor.documents add constraint ux_documents_id_company unique (id, company_id);
alter table vendor.uploads
    add constraint fk_uploads_document foreign key (document_id, company_id) references vendor.documents (id, company_id);
