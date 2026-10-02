-- W-36 (ADR-0012 addendum 2026-10-03, pentest I-2): the worker's functions become rights of the worker's own role
-- (platform 0008) instead of a context rule that a platform console session met as well. EXECUTE moves from erp_app to
-- erp_worker for the upload cleanup (vendor.stale_uploads, vendor.claim_stale_upload, vendor.remove_stale_upload, 0014),
-- the retry scan's queue (vendor.pending_scan_documents, 0014) and the dispute alert job (vendor.unalerted_cr_disputes,
-- vendor.mark_cr_disputes_alerted, 0019). Each keeps its context rule as defence in depth. The console's own dispute
-- functions (open_cr_disputes, upheld_disputes_needing_idp and the rest) stay with erp_app. No new table.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)) then
        raise exception 'Run the vendors migration as a role with BYPASSRLS (or the superuser): its security-definer functions read across row-level security as their owner.';
    end if;

    if not exists (select 1 from pg_roles where rolname = 'erp_worker') then
        raise exception 'Run the platform migrations first: platform 0008 creates erp_worker.';
    end if;
end
$$;

revoke all on function vendor.stale_uploads() from public;
revoke all on function vendor.claim_stale_upload(uuid) from public;
revoke all on function vendor.remove_stale_upload(uuid) from public;
revoke all on function vendor.pending_scan_documents(integer) from public;
revoke all on function vendor.unalerted_cr_disputes(integer) from public;
revoke all on function vendor.mark_cr_disputes_alerted(uuid[]) from public;

revoke all on function vendor.stale_uploads() from erp_app;
revoke all on function vendor.claim_stale_upload(uuid) from erp_app;
revoke all on function vendor.remove_stale_upload(uuid) from erp_app;
revoke all on function vendor.pending_scan_documents(integer) from erp_app;
revoke all on function vendor.unalerted_cr_disputes(integer) from erp_app;
revoke all on function vendor.mark_cr_disputes_alerted(uuid[]) from erp_app;

grant execute on function vendor.stale_uploads() to erp_worker;
grant execute on function vendor.claim_stale_upload(uuid) to erp_worker;
grant execute on function vendor.remove_stale_upload(uuid) to erp_worker;
grant execute on function vendor.pending_scan_documents(integer) to erp_worker;
grant execute on function vendor.unalerted_cr_disputes(integer) to erp_worker;
grant execute on function vendor.mark_cr_disputes_alerted(uuid[]) to erp_worker;
