using Platform.Modules.Vendors.Registration;

namespace Platform.UnitTests.Vendors;

/// <summary>
/// V-6: one user is told a CR number is taken at most five times in an hour; then the registration answers every number
/// the same way. The memory is bounded like the denial audit throttle's. Since W-34 the counts live in Redis; this is the
/// in-process limit the throttle uses without Redis or while Redis does not answer (<see cref="InProcessWindowCounter"/>).
/// </summary>
public sealed class DuplicateCrThrottleTests
{
    [Fact]
    public void A_user_is_limited_after_five_refusals_until_the_hour_from_the_first_ends()
    {
        var clock = new ManualClock();
        var throttle = AccountCounter(clock);

        for (var i = 0; i < DuplicateCrThrottle.Limit; i++)
        {
            throttle.IsLimited("u1").ShouldBeFalse();
            throttle.Record("u1");
            clock.Advance(TimeSpan.FromMinutes(5));
        }

        throttle.IsLimited("u1").ShouldBeTrue();
        throttle.IsLimited("u2").ShouldBeFalse();

        // The window started at the first refusal, 25 minutes ago.
        clock.Advance(TimeSpan.FromMinutes(34));
        throttle.IsLimited("u1").ShouldBeTrue();
        clock.Advance(TimeSpan.FromMinutes(1));
        throttle.IsLimited("u1").ShouldBeFalse();
        throttle.Count.ShouldBe(0);
    }

    [Fact]
    public void The_memory_is_bounded_and_the_oldest_user_is_evicted_first()
    {
        var throttle = AccountCounter(new ManualClock());
        for (var i = 0; i < DuplicateCrThrottle.Limit; i++)
        {
            throttle.Record("first");
        }

        for (var i = 1; i < DuplicateCrThrottle.Capacity; i++)
        {
            throttle.Record($"user-{i}");
        }

        throttle.Count.ShouldBe(DuplicateCrThrottle.Capacity);
        throttle.IsLimited("first").ShouldBeTrue();

        throttle.Record("one-more");

        throttle.Count.ShouldBe(DuplicateCrThrottle.Capacity);
        throttle.IsLimited("first").ShouldBeFalse();
    }

    private static InProcessWindowCounter AccountCounter(TimeProvider clock) =>
        new(clock, DuplicateCrThrottle.Limit, DuplicateCrThrottle.Window, DuplicateCrThrottle.Capacity);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
