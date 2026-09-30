using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Shared.Telemetry;
using Platform.Web.Account;
using Platform.Web.Usage;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-10 business metrics, concurrent users (spec 6.3, O-20): one more circuit handler keeps the connected circuits of this
/// web instance in memory; the gauges <c>waslabid.circuits.connected</c> and <c>waslabid.users.concurrent</c> read them per
/// tenant slug and kind, never with a user or company id. Handlers are driven directly, as the circuit revalidation tests
/// drive the session guard; the gauges are read through a meter listener on this test's own meter factory.
/// </summary>
public sealed class ConcurrentUsersTests : IDisposable
{
    private static readonly (string, string) AcmeTag = (TelemetryNames.Tags.TenantSlug, "acme");
    private static readonly (string, string) StaffTag = (TelemetryNames.Tags.UserKind, TelemetryNames.UserKinds.Staff);
    private static readonly (string, string) VendorTag = (TelemetryNames.Tags.UserKind, TelemetryNames.UserKinds.Vendor);

    private readonly ServiceProvider _meterHost = UsageMetrics.NewMeterFactoryHost();
    private readonly ConnectedCircuits _registry;
    private readonly UsageMetrics _metrics;

    public ConcurrentUsersTests()
    {
        var meters = _meterHost.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        _registry = new ConnectedCircuits(meters);
        _metrics = new UsageMetrics(meters);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_connected_staff_circuit_counts_as_one_concurrent_user_of_its_tenant()
    {
        var (handler, _) = Circuit(UsageSession.Staff("staff-1", TestTenants.Acme));

        await handler.OnConnectionUpAsync(null!, Ct);

        var points = _metrics.Collect();
        var users = points.Where(p => p.Name == TelemetryNames.UsersConcurrent).ShouldHaveSingleItem();
        users.Value.ShouldBe(1);
        users.HasExactly(AcmeTag, StaffTag).ShouldBeTrue("the tags are exactly the tenant slug and the kind");
        var circuits = points.Where(p => p.Name == TelemetryNames.CircuitsConnected).ShouldHaveSingleItem();
        circuits.Value.ShouldBe(1);
        circuits.HasExactly(AcmeTag, StaffTag).ShouldBeTrue();
    }

    [Fact]
    public async Task A_user_with_two_tabs_counts_once()
    {
        // Spec 10: a staff user of acme with two tabs open and a vendor user of acme with one.
        var (first, _) = Circuit(UsageSession.Staff("staff-1", TestTenants.Acme));
        var (second, _) = Circuit(UsageSession.Staff("staff-1", TestTenants.Acme));
        var (vendor, _) = Circuit(UsageSession.VendorUser("vendor-1", TestTenants.Acme, Guid.NewGuid()));

        await first.OnConnectionUpAsync(null!, Ct);
        await second.OnConnectionUpAsync(null!, Ct);
        await vendor.OnConnectionUpAsync(null!, Ct);

        _metrics.Value(TelemetryNames.CircuitsConnected, AcmeTag, StaffTag).ShouldBe(2);
        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, StaffTag).ShouldBe(1);
        _metrics.Value(TelemetryNames.CircuitsConnected, AcmeTag, VendorTag).ShouldBe(1);
        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, VendorTag).ShouldBe(1);

        // One tab closes: one circuit fewer at the next collection, the user still online.
        await second.OnCircuitClosedAsync(null!, Ct);

        _metrics.Value(TelemetryNames.CircuitsConnected, AcmeTag, StaffTag).ShouldBe(1);
        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, StaffTag).ShouldBe(1);
    }

    [Fact]
    public async Task A_closed_circuit_no_longer_counts()
    {
        var (handler, _) = Circuit(UsageSession.Staff("staff-1", TestTenants.Acme));
        await handler.OnConnectionUpAsync(null!, Ct);

        await handler.OnConnectionDownAsync(null!, Ct);
        await handler.OnCircuitClosedAsync(null!, Ct);

        // A tenant and kind seen before reports zero rather than disappearing, so the series drops to 0.
        _metrics.Value(TelemetryNames.CircuitsConnected, AcmeTag, StaffTag).ShouldBe(0);
        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, StaffTag).ShouldBe(0);
    }

    [Fact]
    public async Task A_disconnected_circuit_stops_counting_and_counts_again_on_reconnection()
    {
        var (handler, _) = Circuit(UsageSession.Staff("staff-1", TestTenants.Acme));
        await handler.OnConnectionUpAsync(null!, Ct);

        await handler.OnConnectionDownAsync(null!, Ct);
        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, StaffTag).ShouldBe(0, "a circuit held for reconnection does not count");

        await handler.OnConnectionUpAsync(null!, Ct);
        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, StaffTag).ShouldBe(1);
        _metrics.Value(TelemetryNames.CircuitsConnected, AcmeTag, StaffTag).ShouldBe(1, "the reconnection is the same circuit, counted once");
    }

    [Fact]
    public async Task Vendor_and_platform_circuits_count_under_their_own_kind()
    {
        var (vendor, _) = Circuit(UsageSession.VendorUser("vendor-1", TestTenants.Acme, Guid.NewGuid()));
        var (platform, _) = Circuit(UsageSession.PlatformAdmin("platform-1"));

        await vendor.OnConnectionUpAsync(null!, Ct);
        await platform.OnConnectionUpAsync(null!, Ct);

        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, VendorTag).ShouldBe(1);
        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, StaffTag).ShouldBeNull("no staff circuit was ever seen");
        // Platform users carry no tenant tag (spec 6.1).
        _metrics.Value(TelemetryNames.UsersConcurrent, (TelemetryNames.Tags.UserKind, TelemetryNames.UserKinds.Platform)).ShouldBe(1);
        _metrics.Value(TelemetryNames.CircuitsConnected, (TelemetryNames.Tags.UserKind, TelemetryNames.UserKinds.Platform)).ShouldBe(1);
    }

    [Fact]
    public async Task Anonymous_and_applicant_circuits_are_not_counted()
    {
        var applicant = UsageSession.OnTenant(
            UsageSession.Principal("applicant", ["acme"], [Platform.Modules.Identity.Contracts.IdentityClaims.VendorRealmRole]), TestTenants.Acme);
        var (anonymousCircuit, _) = Circuit(UsageSession.Anonymous(TestTenants.Acme));
        var (applicantCircuit, _) = Circuit(applicant);

        await anonymousCircuit.OnConnectionUpAsync(null!, Ct);
        await applicantCircuit.OnConnectionUpAsync(null!, Ct);

        _metrics.Collect().ShouldBeEmpty();
        _registry.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_circuit_whose_session_ended_no_longer_counts()
    {
        var (handler, guard) = Circuit(UsageSession.Staff("staff-1", TestTenants.Acme));
        await handler.OnConnectionUpAsync(null!, Ct);
        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, StaffTag).ShouldBe(1);

        // W-21: the membership check failed; the circuit stays connected until the reload, but no longer counts.
        guard.End();

        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, StaffTag).ShouldBe(0);
        _metrics.Value(TelemetryNames.CircuitsConnected, AcmeTag, StaffTag).ShouldBe(0);

        // A reconnection of the ended circuit does not count either.
        await handler.OnConnectionDownAsync(null!, Ct);
        await handler.OnConnectionUpAsync(null!, Ct);
        _metrics.Value(TelemetryNames.UsersConcurrent, AcmeTag, StaffTag).ShouldBe(0);
    }

    [Fact]
    public async Task No_usage_metric_carries_a_user_or_company_id()
    {
        var company = Guid.NewGuid();
        const string staffSub = "7d7c1a0e-3f0b-4b53-9d6e-0a1f2b3c4d5e";
        const string vendorSub = "0b1c2d3e-4f50-4617-8899-aabbccddeeff";
        await Circuit(UsageSession.Staff(staffSub, TestTenants.Acme)).Handler.OnConnectionUpAsync(null!, Ct);
        await Circuit(UsageSession.VendorUser(vendorSub, TestTenants.Acme, company)).Handler.OnConnectionUpAsync(null!, Ct);
        await Circuit(UsageSession.PlatformAdmin("platform-sub")).Handler.OnConnectionUpAsync(null!, Ct);

        var points = _metrics.Collect();

        points.ShouldNotBeEmpty();
        string[] allowed = [TelemetryNames.Tags.TenantSlug, TelemetryNames.Tags.UserKind, TelemetryNames.Tags.Window];
        points.SelectMany(p => p.Tags.Keys).Distinct().ShouldAllBe(key => allowed.Contains(key));
        var values = points.SelectMany(p => p.Tags.Values).ToList();
        foreach (var secret in new[] { staffSub, vendorSub, "platform-sub", company.ToString(), TestTenants.Acme.TenantId.ToString(), $"{staffSub}@usage.test" })
        {
            values.ShouldNotContain(secret);
        }
    }

    [Fact]
    public async Task The_snapshot_gives_circuits_and_distinct_users_per_tenant_and_kind()
    {
        await Circuit(UsageSession.Staff("staff-1", TestTenants.Acme)).Handler.OnConnectionUpAsync(null!, Ct);
        await Circuit(UsageSession.Staff("staff-1", TestTenants.Acme)).Handler.OnConnectionUpAsync(null!, Ct);
        await Circuit(UsageSession.Staff("staff-2", TestTenants.Acme)).Handler.OnConnectionUpAsync(null!, Ct);
        await Circuit(UsageSession.Staff("staff-3", TestTenants.Beta)).Handler.OnConnectionUpAsync(null!, Ct);
        await Circuit(UsageSession.VendorUser("vendor-1", TestTenants.Beta, Guid.NewGuid())).Handler.OnConnectionUpAsync(null!, Ct);

        var snapshot = _registry.Snapshot();

        snapshot.ShouldBe(
            [
                new ConnectedCount("acme", UsageKind.Staff, Circuits: 3, Users: 2),
                new ConnectedCount("beta", UsageKind.Staff, Circuits: 1, Users: 1),
                new ConnectedCount("beta", UsageKind.Vendor, Circuits: 1, Users: 1),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Runs_after_the_session_guard()
    {
        var (handler, guard) = Circuit(UsageSession.Staff("staff-1", TestTenants.Acme));

        handler.Order.ShouldBe(int.MinValue + 3);
        handler.Order.ShouldBeGreaterThan(guard.Order);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _meterHost.Dispose();
    }

    private (UsageCircuitHandler Handler, CircuitSessionGuard Guard) Circuit(UsageSession session)
    {
        var guard = session.Guard();
        return (new UsageCircuitHandler(session.Connection(), session.Tenants, session.Vendor, session.Platform, guard, _registry, new NoActivity()), guard);
    }

    /// <summary>Concurrent users only; recording activity is the active-user tests' concern.</summary>
    private sealed class NoActivity : Platform.Modules.Identity.Contracts.IUserActivityRecorder
    {
        public Task RecordAsync(Platform.Modules.Identity.Contracts.ActivityKind kind, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
