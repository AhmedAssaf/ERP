using Platform.Modules.Identity;

namespace Platform.UnitTests.Identity;

/// <summary>
/// Denials are audited once per tenant, action, user, host and path per minute (spec 4.1; W-27), so a page that evaluates
/// a policy several times, or a user retrying, cannot flood the tenant's audit log.
/// </summary>
public sealed class DenialAuditThrottleTests
{
    private static readonly Guid Tenant = Guid.Parse("0f0e0d0c-0000-7000-8000-00000000ac01");

    [Fact]
    public void The_same_denial_is_audited_once_per_minute()
    {
        var clock = new ManualClock();
        var throttle = new DenialAuditThrottle(clock);
        var key = new DenialKey(Tenant, "identity.role_denied", "u1", "acme.localhost", "/admin");

        throttle.ShouldAudit(key).ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(59));
        throttle.ShouldAudit(key).ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        throttle.ShouldAudit(key).ShouldBeTrue();
        throttle.ShouldAudit(key).ShouldBeFalse();
        DenialAuditThrottle.Window.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Each_part_of_the_key_makes_a_separate_denial()
    {
        var throttle = new DenialAuditThrottle(new ManualClock());
        var key = new DenialKey(Tenant, "identity.role_denied", "u1", "acme.localhost", "/admin");
        throttle.ShouldAudit(key).ShouldBeTrue();

        throttle.ShouldAudit(key with { TenantId = Guid.NewGuid() }).ShouldBeTrue();
        throttle.ShouldAudit(key with { Action = "identity.cross_tenant_denied" }).ShouldBeTrue();
        throttle.ShouldAudit(key with { UserId = "u2" }).ShouldBeTrue();
        throttle.ShouldAudit(key with { UserId = null }).ShouldBeTrue();
        throttle.ShouldAudit(key with { Host = "beta.localhost" }).ShouldBeTrue();
        throttle.ShouldAudit(key with { Path = "/admin/staff" }).ShouldBeTrue();
    }

    [Fact]
    public void Expired_entries_are_dropped_so_memory_stays_bounded()
    {
        var clock = new ManualClock();
        var throttle = new DenialAuditThrottle(clock);
        for (var i = 0; i <= DenialAuditThrottle.PruneAbove; i++)
        {
            throttle.ShouldAudit(new DenialKey(Tenant, "identity.role_denied", $"u{i}", "acme.localhost", "/admin"));
        }

        clock.Advance(DenialAuditThrottle.Window);
        throttle.ShouldAudit(new DenialKey(Tenant, "identity.role_denied", "late", "acme.localhost", "/admin")).ShouldBeTrue();

        throttle.Count.ShouldBe(1);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
