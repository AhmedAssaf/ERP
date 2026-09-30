-- W-10 business metrics (spec docs/superpowers/specs/2026-09-30-observability-design.md section 6.6, O-23): the usage
-- job's latest active-user counts, replaced every five minutes in one transaction, read by the platform console's usage
-- page through IUsageLog (the IHealthLog pattern). A platform aggregate like the rest of the ops schema: no tenant_id and
-- no personal data, only the tenant slug (null for the across-tenants rows), the kind, the window and the count.
--
-- Beyond the spec's grants: forced row-level security with one policy for every command, readable and writable only by a
-- session with neither a tenant nor a vendor context (the worker and the platform console), as ops.platform_audit is
-- read (operations 0004, ADR-0012 point 4). The rows are counts, but they are every tenant's counts, and a tenant or
-- vendor session on a tenant host has no business reading its competitors' activity.

create table ops.active_user_counts (
    tenant_slug text        null,
    kind        text        not null check (kind in ('staff', 'vendor')),
    time_window text        not null check (time_window in ('1d', '7d', '30d')),
    users       integer     not null check (users >= 0),
    computed_at timestamptz not null,
    constraint ux_active_user_counts unique nulls not distinct (tenant_slug, kind, time_window)
);

alter table ops.active_user_counts enable row level security;
alter table ops.active_user_counts force row level security;
create policy platform_only on ops.active_user_counts
    using (platform.current_tenant() is null and platform.current_vendor_company() is null)
    with check (platform.current_tenant() is null and platform.current_vendor_company() is null);

grant select, insert, delete on ops.active_user_counts to erp_app;
