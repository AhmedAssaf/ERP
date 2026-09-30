using Platform.Modules.Identity.Activity;
using Platform.Modules.Identity.Contracts;

namespace Platform.UnitTests.Identity;

/// <summary>
/// W-10 (spec 6.4): the activity recorder writes at most once an hour per tenant, user and kind; the throttle lets the
/// first call of each hour through and forgets entries older than two hours, so its memory stays bounded.
/// </summary>
public sealed class ActivityThrottleTests
{
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly Guid Beta = Guid.NewGuid();

    [Fact]
    public void The_first_call_of_each_hour_passes_per_tenant_user_and_kind()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 30, 9, 10, 0, TimeSpan.Zero));
        var throttle = new ActivityThrottle(clock);

        throttle.TryEnter(Acme, "u1", ActivityKind.Staff, out _).ShouldBeTrue();
        throttle.TryEnter(Acme, "u1", ActivityKind.Staff, out _).ShouldBeFalse();
        throttle.TryEnter(Acme, "u1", ActivityKind.Vendor, out _).ShouldBeTrue("another kind");
        throttle.TryEnter(Beta, "u1", ActivityKind.Staff, out _).ShouldBeTrue("another tenant");
        throttle.TryEnter(Acme, "u2", ActivityKind.Staff, out _).ShouldBeTrue("another user");

        clock.Now = clock.Now.AddMinutes(49);
        throttle.TryEnter(Acme, "u1", ActivityKind.Staff, out _).ShouldBeFalse("09:59 is the same hour");

        clock.Now = clock.Now.AddMinutes(1);
        throttle.TryEnter(Acme, "u1", ActivityKind.Staff, out _).ShouldBeTrue("10:00 is a new hour");
    }

    [Fact]
    public void A_forgotten_entry_passes_again()
    {
        var throttle = new ActivityThrottle(new ManualClock(DateTimeOffset.UtcNow));
        throttle.TryEnter(Acme, "u1", ActivityKind.Staff, out var entry).ShouldBeTrue();

        throttle.Forget(entry);

        throttle.TryEnter(Acme, "u1", ActivityKind.Staff, out _).ShouldBeTrue();
    }

    [Fact]
    public void Entries_older_than_two_hours_are_dropped()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero));
        var throttle = new ActivityThrottle(clock);
        for (var i = 0; i < 100; i++)
        {
            throttle.TryEnter(Acme, $"user-{i}", ActivityKind.Staff, out _);
        }

        throttle.Count.ShouldBe(100);
        clock.Now = clock.Now.AddHours(1);
        throttle.TryEnter(Acme, "late", ActivityKind.Staff, out _);
        throttle.Count.ShouldBe(101, "the previous hour is kept");

        clock.Now = clock.Now.AddHours(2);
        throttle.TryEnter(Acme, "later", ActivityKind.Staff, out _);
        throttle.Count.ShouldBe(1);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
