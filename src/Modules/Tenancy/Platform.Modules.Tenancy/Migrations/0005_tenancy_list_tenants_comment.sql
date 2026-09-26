-- Records the trust model on the function itself (review of plan task 7), since 0004 is applied and checksummed.
comment on function tenancy.list_tenants() is
    'Every tenant, for the platform console only (F-54). Executable by erp_app, the role every request connects as, so '
    'the database cannot tell a console request from a tenant request: like tenancy.resolve_host, access is gated in '
    'application code, not in the database. ITenantCatalog refuses unless the scope is a platform request, and the '
    'console pages also require the PlatformAdmin policy.';
