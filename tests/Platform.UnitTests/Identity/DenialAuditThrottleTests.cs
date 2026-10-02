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
        for (var i = 0; i <= DenialAuditThrottle.Capacity; i++)
        {
            throttle.ShouldAudit(new DenialKey(Tenant, "identity.role_denied", $"u{i}", "acme.localhost", "/admin"));
        }

        clock.Advance(DenialAuditThrottle.Window);
        throttle.ShouldAudit(new DenialKey(Tenant, "identity.role_denied", "late", "acme.localhost", "/admin")).ShouldBeTrue();

        throttle.Count.ShouldBe(1);
    }

    [Fact]
    public void Distinct_denials_within_one_window_stay_under_the_cap()
    {
        var throttle = new DenialAuditThrottle(new ManualClock());
        for (var i = 0; i < 50_000; i++)
        {
            throttle.ShouldAudit(new DenialKey(Tenant, "identity.role_denied", $"u{i}", "acme.localhost", "/admin")).ShouldBeTrue();
            throttle.Count.ShouldBeLessThanOrEqualTo(DenialAuditThrottle.Capacity);
        }

        DenialAuditThrottle.Capacity.ShouldBe(10_000);
        throttle.Count.ShouldBe(DenialAuditThrottle.Capacity);
    }

    /// <summary>
    /// No call scans the map. Wall-clock limits fail on a loaded machine, so this compares the same work on two map sizes
    /// in the same run: the fastest of 20 batches of 1,000 new denials on a map of one entry (the clock passes a window
    /// before each, so the previous entry expires and is dropped) against the fastest on a full map of 10,000 (each new
    /// denial evicts the oldest). Both drop one entry and add one; a scan of the map would make the full case about
    /// 10,000 times slower, so a factor of 10 (plus a millisecond for timer resolution) separates them on any machine.
    /// </summary>
    [Fact]
    public void A_full_map_costs_about_what_an_empty_one_costs_per_denial()
    {
        const int Batches = 20;
        const int PerBatch = 1_000;

        var smallClock = new ManualClock();
        var small = new DenialAuditThrottle(smallClock);
        var fullClock = new ManualClock();
        var full = new DenialAuditThrottle(fullClock);
        for (var i = 0; i < DenialAuditThrottle.Capacity; i++)
        {
            full.ShouldAudit(Key("seed", i));
        }

        full.Count.ShouldBe(DenialAuditThrottle.Capacity);
        var smallKeys = Keys("small", Batches * PerBatch);
        var fullKeys = Keys("full", Batches * PerBatch);
        var smallStep = DenialAuditThrottle.Window;
        var fullStep = TimeSpan.FromTicks(1);
        Measure(small, smallClock, smallStep, Keys("warm-small", PerBatch), 0);
        Measure(full, fullClock, fullStep, Keys("warm-full", PerBatch), 0);

        var minSmall = TimeSpan.MaxValue;
        var minFull = TimeSpan.MaxValue;
        for (var batch = 0; batch < Batches; batch++)
        {
            var smallTime = Measure(small, smallClock, smallStep, smallKeys, batch * PerBatch);
            var fullTime = Measure(full, fullClock, fullStep, fullKeys, batch * PerBatch);
            minSmall = smallTime < minSmall ? smallTime : minSmall;
            minFull = fullTime < minFull ? fullTime : minFull;
        }

        small.Count.ShouldBe(1);
        full.Count.ShouldBe(DenialAuditThrottle.Capacity);
        minFull.ShouldBeLessThan(
            (minSmall * 10) + TimeSpan.FromMilliseconds(1),
            $"best batch on a full map {minFull.TotalMilliseconds:F3} ms, on a one-entry map {minSmall.TotalMilliseconds:F3} ms");

        static TimeSpan Measure(DenialAuditThrottle throttle, ManualClock clock, TimeSpan step, DenialKey[] keys, int from)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = from; i < from + PerBatch && i < keys.Length; i++)
            {
                clock.Advance(step);
                if (!throttle.ShouldAudit(keys[i]))
                {
                    throw new InvalidOperationException("Every denial in the batch is new and must be audited.");
                }
            }

            watch.Stop();
            return watch.Elapsed;
        }

        static DenialKey Key(string prefix, int i) => new(Tenant, "identity.role_denied", $"{prefix}-{i}", "acme.localhost", "/admin");

        static DenialKey[] Keys(string prefix, int count) => [.. Enumerable.Range(0, count).Select(i => Key(prefix, i))];
    }

    [Fact]
    public void When_full_the_oldest_denial_is_evicted_first()
    {
        var clock = new ManualClock();
        var throttle = new DenialAuditThrottle(clock);
        DenialKey Key(int i) => new(Tenant, "identity.role_denied", $"u{i}", "acme.localhost", "/admin");
        for (var i = 0; i < DenialAuditThrottle.Capacity; i++)
        {
            throttle.ShouldAudit(Key(i));
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        throttle.ShouldAudit(Key(DenialAuditThrottle.Capacity)).ShouldBeTrue();

        throttle.Count.ShouldBe(DenialAuditThrottle.Capacity);
        // u0 was evicted, so it is audited again; u1 is still remembered.
        throttle.ShouldAudit(Key(1)).ShouldBeFalse();
        throttle.ShouldAudit(Key(0)).ShouldBeTrue();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
