using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Platform.Shared.Telemetry;

namespace Platform.Web.Usage;

/// <summary>Connected circuits and distinct users of one tenant (null for platform users) and kind, on this web instance.</summary>
internal sealed record ConnectedCount(string? TenantSlug, UsageKind Kind, int Circuits, int Users);

/// <summary>
/// The circuits whose connection is up now on this web instance (spec 6.3, O-20), kept by <see cref="UsageCircuitHandler"/>:
/// per circuit its tenant slug, its kind and its user's <c>sub</c>. The <c>sub</c> stays in process memory while the
/// connection is up and is only ever counted, never exported (O-19, spec 6.8). A circuit whose session W-21 has ended
/// leaves the registry at the next read, even while its connection is still up.
/// <para>
/// Publishes <see cref="TelemetryNames.CircuitsConnected"/> and <see cref="TelemetryNames.UsersConcurrent"/> on meter
/// <see cref="TelemetryNames.UsageMeter"/> as observable gauges, read when metrics are collected; a tenant and kind seen
/// once since the process started keeps reporting (zero when nobody is connected), so its series drops to zero instead of
/// going stale. Only the tenant slug and the kind are tags; platform users carry no tenant tag. The console usage page
/// reads <see cref="Snapshot"/> directly (spec 6.6). Per instance: with several web instances each knows only its own.
/// </para>
/// </summary>
internal sealed class ConnectedCircuits
{
    private readonly ConcurrentDictionary<object, Entry> _circuits = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<(string? TenantSlug, UsageKind Kind), byte> _seen = new();

    public ConnectedCircuits(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        // The meter belongs to the factory, which disposes it with the host.
        var meter = meters.Create(TelemetryNames.UsageMeter);
        meter.CreateObservableGauge(
            TelemetryNames.CircuitsConnected, () => Measure(c => c.Circuits), TelemetryNames.Units.Circuits,
            "Blazor circuits whose connection is up now, on this web instance.");
        meter.CreateObservableGauge(
            TelemetryNames.UsersConcurrent, () => Measure(c => c.Users), TelemetryNames.Units.Users,
            "Distinct signed-in users with at least one connected circuit, on this web instance.");
    }

    /// <summary>Registers a connected circuit, or replaces its entry. <paramref name="ended"/> says whether its session ended.</summary>
    public void Add(object circuit, string? tenantSlug, UsageKind kind, string userId, Func<bool> ended)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(ended);
        _circuits[circuit] = new Entry(kind == UsageKind.Platform ? null : tenantSlug, kind, userId, ended);
        _seen.TryAdd((kind == UsageKind.Platform ? null : tenantSlug, kind), 0);
    }

    public void Remove(object circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        _circuits.TryRemove(circuit, out _);
    }

    /// <summary>Circuits and distinct users per tenant slug and kind now; only groups with a connected circuit.</summary>
    public IReadOnlyList<ConnectedCount> Snapshot()
    {
        foreach (var (circuit, entry) in _circuits)
        {
            if (entry.Ended())
            {
                _circuits.TryRemove(circuit, out _);
            }
        }

        return
        [
            .. _circuits.Values
                .GroupBy(e => (e.TenantSlug, e.Kind))
                .Select(g => new ConnectedCount(g.Key.TenantSlug, g.Key.Kind, g.Count(), g.Select(e => e.UserId).Distinct(StringComparer.Ordinal).Count())),
        ];
    }

    private List<Measurement<int>> Measure(Func<ConnectedCount, int> value)
    {
        var now = Snapshot().ToDictionary(c => (c.TenantSlug, c.Kind));
        return
        [
            .. _seen.Keys.Select(key => new Measurement<int>(now.TryGetValue(key, out var count) ? value(count) : 0, Tags(key.TenantSlug, key.Kind))),
        ];
    }

    private static KeyValuePair<string, object?>[] Tags(string? tenantSlug, UsageKind kind) => tenantSlug is null
        ? [new(TelemetryNames.Tags.UserKind, UsageKinds.TagValue(kind))]
        : [new(TelemetryNames.Tags.TenantSlug, tenantSlug), new(TelemetryNames.Tags.UserKind, UsageKinds.TagValue(kind))];

    private sealed record Entry(string? TenantSlug, UsageKind Kind, string UserId, Func<bool> Ended);
}
