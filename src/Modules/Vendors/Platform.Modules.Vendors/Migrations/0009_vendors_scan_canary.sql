-- Vendor scan canary (final review of vendor plan task 3; V-10). No schema change for the canary itself, which is code
-- in the retry job. This script records, on the Operations module's table, the one writer outside that module:
-- vendor.unpark_document (0007, audited since 0008) inserts into ops.platform_audit directly, as the migration owner,
-- with a version 4 id from gen_random_uuid(). Applied migrations are checksummed, so the comment is set here.

comment on table ops.platform_audit is
    'Platform audit (Operations module), append-only for erp_app. Written through IPlatformAudit, except vendor.unpark_document (vendors migrations 0007 and 0008), which inserts vendor.document_unparked directly as the migration owner with a version 4 id; it moves to the platform console through IPlatformAudit later.';
