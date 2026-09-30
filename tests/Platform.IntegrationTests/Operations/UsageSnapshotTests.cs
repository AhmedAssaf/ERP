using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Identity;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Operations.Usage;
using Platform.Shared.Telemetry;

namespace Platform.IntegrationTests.Operations;

/// <summary>
/// The usage job's last result in the worker's memory (spec 6.4): its observable gauges report what the job stored, and
/// nothing once the result is older than 15 minutes, so a stopped job shows as a gap rather than a flat line.
/// </summary>
public sealed class UsageSnapshotTests : IDisposable
{
    private readonly ServiceProvider _meterHost = UsageMetrics.NewMeterFactoryHost();

    public void Dispose() => _meterHost.Dispose();

    [Fact]
    public void A_fresh_result_is_reported_per_tenant_and_across_tenants()
    {
        var clock = new TestClock();
        var meters = _meterHost.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        var snapshot = new UsageSnapshot(meters, clock);
        using var metrics = new UsageMetrics(meters);

        snapshot.Set(new UsageCounts(clock.GetUtcNow(),
        [
            new ActiveUserCount("acme", TelemetryNames.UserKinds.Staff, TelemetryNames.Windows.OneDay, 3),
            new ActiveUserCount(null, TelemetryNames.UserKinds.Vendor, TelemetryNames.Windows.ThirtyDays, 7),
        ]));

        metrics.Value(
            TelemetryNames.UsersActive,
            (TelemetryNames.Tags.TenantSlug, "acme"), (TelemetryNames.Tags.UserKind, "staff"), (TelemetryNames.Tags.Window, "1d")).ShouldBe(3);
        metrics.Value(
            TelemetryNames.UsersActiveAllTenants, (TelemetryNames.Tags.UserKind, "vendor"), (TelemetryNames.Tags.Window, "30d")).ShouldBe(7);
        metrics.Collect().Count.ShouldBe(2);
    }

    [Fact]
    public void A_stale_usage_result_is_not_reported()
    {
        var clock = new TestClock();
        var meters = _meterHost.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        var snapshot = new UsageSnapshot(meters, clock);
        using var metrics = new UsageMetrics(meters);
        snapshot.Set(new UsageCounts(clock.GetUtcNow(), [new ActiveUserCount("acme", "staff", "1d", 3)]));

        clock.Advance(UsageCounts.StaleAfter + TimeSpan.FromSeconds(1));

        metrics.Collect().ShouldBeEmpty();
        snapshot.Current.ShouldBeNull();
    }

    [Fact]
    public void Before_the_first_run_nothing_is_reported()
    {
        var meters = _meterHost.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        _ = new UsageSnapshot(meters, new TestClock());
        using var metrics = new UsageMetrics(meters);

        metrics.Collect().ShouldBeEmpty();
    }
}
