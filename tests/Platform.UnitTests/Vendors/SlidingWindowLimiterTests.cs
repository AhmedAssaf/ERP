using Platform.Modules.Vendors.RateLimiting;

namespace Platform.UnitTests.Vendors;

/// <summary>
/// W-35, W-37: the in-process limiter behind the vendor join and consent limits. Each key gets at most its limit of
/// permits in any window (a sliding log, so no burst of twice the limit across a window boundary), keys never share a
/// count, and the memory is bounded like the duplicate-CR throttle's.
/// </summary>
public sealed class SlidingWindowLimiterTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public void A_key_gets_its_limit_and_then_is_refused_while_another_key_is_not()
    {
        var limiter = new SlidingWindowLimiter(3, Minute, new ManualClock());

        limiter.TryAcquire("a").ShouldBeTrue();
        limiter.TryAcquire("a").ShouldBeTrue();
        limiter.TryAcquire("a").ShouldBeTrue();
        limiter.TryAcquire("a").ShouldBeFalse();

        limiter.TryAcquire("b").ShouldBeTrue();
        limiter.TryAcquire("a").ShouldBeFalse();
    }

    [Fact]
    public void A_permit_comes_back_one_window_after_the_oldest_permit_not_at_a_fixed_boundary()
    {
        var clock = new ManualClock();
        var limiter = new SlidingWindowLimiter(2, Minute, clock);

        limiter.TryAcquire("a").ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(50));
        limiter.TryAcquire("a").ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(9));
        limiter.TryAcquire("a").ShouldBeFalse("59 s after the first permit, two are still in the window");

        // The first permit leaves the window at 60 s; the second is still in it, so one more and no more.
        clock.Advance(TimeSpan.FromSeconds(1));
        limiter.TryAcquire("a").ShouldBeTrue();
        limiter.TryAcquire("a").ShouldBeFalse();

        // A refusal does not count: once the second permit (at 50 s) leaves the window, one is free again.
        clock.Advance(TimeSpan.FromSeconds(50));
        limiter.TryAcquire("a").ShouldBeTrue();
        limiter.TryAcquire("a").ShouldBeFalse();
    }

    [Fact]
    public void After_a_full_window_without_use_the_whole_limit_is_available_and_the_key_is_forgotten()
    {
        var clock = new ManualClock();
        var limiter = new SlidingWindowLimiter(2, Minute, clock);
        limiter.TryAcquire("a").ShouldBeTrue();
        limiter.TryAcquire("a").ShouldBeTrue();
        limiter.TryAcquire("a").ShouldBeFalse();

        clock.Advance(Minute);

        limiter.TryAcquire("b").ShouldBeTrue();
        limiter.Count.ShouldBe(1, "a's last use is a full window old, so it is dropped");
        limiter.TryAcquire("a").ShouldBeTrue();
        limiter.TryAcquire("a").ShouldBeTrue();
        limiter.TryAcquire("a").ShouldBeFalse();
    }

    [Fact]
    public void The_memory_is_bounded_and_the_least_recently_used_key_is_evicted_first()
    {
        var limiter = new SlidingWindowLimiter(1, Minute, new ManualClock(), capacity: 3);
        limiter.TryAcquire("first").ShouldBeTrue();
        limiter.TryAcquire("second").ShouldBeTrue();
        limiter.TryAcquire("third").ShouldBeTrue();

        // A refused attempt is a use too: "first" is now the most recently used, "second" the least.
        limiter.TryAcquire("first").ShouldBeFalse();
        limiter.TryAcquire("fourth").ShouldBeTrue();

        limiter.Count.ShouldBe(3);
        limiter.TryAcquire("first").ShouldBeFalse("still remembered");
        limiter.TryAcquire("second").ShouldBeTrue("evicted, so it starts again");
    }

    [Fact]
    public void Parallel_callers_never_get_more_than_the_limit()
    {
        var limiter = new SlidingWindowLimiter(50, Minute, new ManualClock());
        var granted = 0;

        Parallel.For(0, 1_000, _ =>
        {
            if (limiter.TryAcquire("shared"))
            {
                Interlocked.Increment(ref granted);
            }
        });

        granted.ShouldBe(50);
    }

    [Fact]
    public void A_limit_or_window_that_is_not_positive_and_a_blank_key_are_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SlidingWindowLimiter(0, Minute, new ManualClock()));
        Should.Throw<ArgumentOutOfRangeException>(() => new SlidingWindowLimiter(1, TimeSpan.Zero, new ManualClock()));
        Should.Throw<ArgumentOutOfRangeException>(() => new SlidingWindowLimiter(1, Minute, new ManualClock(), capacity: 0));
        Should.Throw<ArgumentException>(() => new SlidingWindowLimiter(1, Minute, new ManualClock()).TryAcquire(" "));
    }

    internal sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
