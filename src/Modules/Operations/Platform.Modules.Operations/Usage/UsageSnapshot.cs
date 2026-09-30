using System.Diagnostics.Metrics;
using Platform.Modules.Operations.Contracts;
using Platform.Shared.Telemetry;

namespace Platform.Modules.Operations.Usage;

/// <summary>
/// The usage job's last result in the worker's memory (spec 6.4), so a scrape never queries the database: publishes
/// <see cref="TelemetryNames.UsersActive"/> (tenant slug, kind, window) and <see cref="TelemetryNames.UsersActiveAllTenants"/>
/// (kind, window) on meter <see cref="TelemetryNames.UsageMeter"/>. A result older than <see cref="UsageCounts.StaleAfter"/>
/// is not reported, so a stopped job shows as a gap rather than a flat line.
/// </summary>
internal sealed class UsageSnapshot
{
    private readonly TimeProvider _clock;
    private volatile UsageCounts? _latest;

    public UsageSnapshot(IMeterFactory meters, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _clock = clock;
        // The meter belongs to the factory, which disposes it with the host.
        var meter = meters.Create(TelemetryNames.UsageMeter);
        meter.CreateObservableGauge(
            TelemetryNames.UsersActive, () => Measure(acrossTenants: false), TelemetryNames.Units.Users,
            "Distinct users with an authenticated request or circuit activity in the window, per tenant.");
        meter.CreateObservableGauge(
            TelemetryNames.UsersActiveAllTenants, () => Measure(acrossTenants: true), TelemetryNames.Units.Users,
            "Distinct users with an authenticated request or circuit activity in the window, across tenants, each user once.");
    }

    /// <summary>The latest result while it is fresh; null before the first run or once it is stale.</summary>
    public UsageCounts? Current => _latest is { } latest && !latest.IsStaleAt(_clock.GetUtcNow()) ? latest : null;

    public void Set(UsageCounts counts) => _latest = counts ?? throw new ArgumentNullException(nameof(counts));

    private List<Measurement<int>> Measure(bool acrossTenants) =>
    [
        .. (Current?.Counts ?? [])
            .Where(c => (c.TenantSlug is null) == acrossTenants)
            .Select(c => new Measurement<int>(c.Users, Tags(c))),
    ];

    private static KeyValuePair<string, object?>[] Tags(ActiveUserCount count) => count.TenantSlug is null
        ? [new(TelemetryNames.Tags.UserKind, count.Kind), new(TelemetryNames.Tags.Window, count.Window)]
        : [new(TelemetryNames.Tags.TenantSlug, count.TenantSlug), new(TelemetryNames.Tags.UserKind, count.Kind), new(TelemetryNames.Tags.Window, count.Window)];
}
