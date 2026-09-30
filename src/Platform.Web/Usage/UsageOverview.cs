using Platform.Modules.Operations.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Telemetry;

namespace Platform.Web.Usage;

/// <summary>Staff and vendors of one figure; a null part is unknown and shown as a dash, and so is the total then.</summary>
internal sealed record UsageFigure(int? Staff, int? Vendors)
{
    public int? Total => Staff is { } staff && Vendors is { } vendors ? staff + vendors : null;
}

/// <summary>One tenant's line of the usage table: online now, and active today, in 7 days and in 30 days.</summary>
internal sealed record TenantUsage(TenantSummary Tenant, UsageFigure Online, UsageFigure Today, UsageFigure Week, UsageFigure Month);

/// <summary>
/// What <c>/platform/usage</c> shows (spec 6.6): the four totals, the per-tenant lines, when the active users were counted
/// (null before the first count), whether that count is stale, and the Grafana dashboard's address when a valid one is
/// configured (<see cref="GrafanaLink"/>).
/// </summary>
internal sealed record UsageView(
    UsageFigure Online,
    UsageFigure Today,
    UsageFigure Week,
    UsageFigure Month,
    IReadOnlyList<TenantUsage> Tenants,
    DateTimeOffset? CountedAt,
    bool Stale,
    string? GrafanaDashboardUrl);

/// <summary>
/// Gathers the console usage page (W-10, spec 6.6, O-23) from what the platform already holds: tenants from
/// <see cref="ITenantCatalog"/> (portal names), concurrent users from this instance's <see cref="ConnectedCircuits"/>, and
/// active users from the usage job's stored result (<see cref="IUsageLog"/>, <c>ops.active_user_counts</c>). It never
/// queries Prometheus, Loki or Grafana and makes no HTTP call, so the page works without the telemetry stack. A stored
/// result older than <see cref="UsageCounts.StaleAfter"/>, or none, makes every active figure unknown; concurrent users
/// are always known. Totals count each user once across tenants (the stored across-tenants rows, and distinct users of
/// the registry). Platform users are not shown: they are the readers. Used only by console pages, after PlatformAdmin passed.
/// </summary>
internal sealed class UsageOverview(
    ITenantCatalog catalog, ConnectedCircuits circuits, IUsageLog usage, TimeProvider clock, GrafanaLink grafana)
{
    public async Task<UsageView> LoadAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await catalog.ListAsync(cancellationToken);
        var latest = await usage.LatestAsync(cancellationToken);
        var stale = latest is null || latest.IsStaleAt(clock.GetUtcNow());
        var counts = stale
            ? null
            : latest!.Counts.ToDictionary(c => (c.TenantSlug ?? string.Empty, c.Kind, c.Window), c => c.Users);
        var online = circuits.Snapshot().ToDictionary(c => (c.TenantSlug ?? string.Empty, c.Kind), c => c.Users);

        UsageFigure Online(string slug) => new(
            online.GetValueOrDefault((slug, UsageKind.Staff)), online.GetValueOrDefault((slug, UsageKind.Vendor)));

        // A fresh result has every tenant, kind and window (zero when nobody was active); a tenant it does not know yet
        // (created after the count) shows zero too, never a guess from elsewhere.
        UsageFigure Active(string? slug, string window) => counts is null
            ? new UsageFigure(null, null)
            : new UsageFigure(
                counts.GetValueOrDefault((slug ?? string.Empty, TelemetryNames.UserKinds.Staff, window)),
                counts.GetValueOrDefault((slug ?? string.Empty, TelemetryNames.UserKinds.Vendor, window)));

        var rows = tenants
            .Select(t => new TenantUsage(
                t,
                Online(t.Slug),
                Active(t.Slug, TelemetryNames.Windows.OneDay),
                Active(t.Slug, TelemetryNames.Windows.SevenDays),
                Active(t.Slug, TelemetryNames.Windows.ThirtyDays)))
            .ToList();

        return new UsageView(
            new UsageFigure(circuits.DistinctUsers(UsageKind.Staff), circuits.DistinctUsers(UsageKind.Vendor)),
            Active(null, TelemetryNames.Windows.OneDay),
            Active(null, TelemetryNames.Windows.SevenDays),
            Active(null, TelemetryNames.Windows.ThirtyDays),
            rows,
            latest?.ComputedAt,
            latest is not null && stale,
            grafana.DashboardUrl);
    }
}
