using System.Collections.Concurrent;
using Platform.Modules.Identity.Contracts;

namespace Platform.Modules.Identity.Activity;

/// <summary>One recorded hour of one user's activity in one tenant and kind.</summary>
internal readonly record struct ActivityEntry(Guid TenantId, string UserId, ActivityKind Kind, DateTimeOffset Hour);

/// <summary>
/// Process-wide: lets only the first activity of each hour per tenant, user and kind through to the database (spec 6.4),
/// by this host's clock. The database keeps its own hour (the insert trigger), and the insert is <c>on conflict do
/// nothing</c>, so another instance, a restart, or a host clock a little off the database's at the turn of the hour only
/// repeats a harmless insert. Entries of the current and the previous hour are kept; older ones are dropped.
/// </summary>
internal sealed class ActivityThrottle(TimeProvider clock)
{
    /// <summary>After a failed write, no request or circuit event of this process touches the database for this long.</summary>
    public static TimeSpan RetryAfter { get; } = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<ActivityEntry, byte> _entries = new();
    private long _sweptHourTicks;
    private long _pausedUntilTicks;

    public int Count => _entries.Count;

    /// <summary>True while a recent failure holds every write back (<see cref="RetryAfter"/>).</summary>
    public bool IsPaused => clock.GetUtcNow().UtcTicks < Interlocked.Read(ref _pausedUntilTicks);

    /// <summary>
    /// Holds writes back for <see cref="RetryAfter"/> after a failure. True only for the failure that starts a new pause, so
    /// the caller logs once per window however many writes fail at the same time.
    /// </summary>
    public bool Pause()
    {
        var now = clock.GetUtcNow().UtcTicks;
        var until = now + RetryAfter.Ticks;
        while (true)
        {
            var current = Interlocked.Read(ref _pausedUntilTicks);
            if (now < current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _pausedUntilTicks, until, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>True for the first call of the hour; <paramref name="entry"/> can then be forgotten if its write fails.</summary>
    public bool TryEnter(Guid tenantId, string userId, ActivityKind kind, out ActivityEntry entry)
    {
        var now = clock.GetUtcNow();
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        Sweep(hour);
        entry = new ActivityEntry(tenantId, userId, kind, hour);
        return _entries.TryAdd(entry, 0);
    }

    /// <summary>Forgets an entry whose write failed, so the next call of that hour tries again.</summary>
    public void Forget(ActivityEntry entry) => _entries.TryRemove(entry, out _);

    // Once per hour: drop what is two hours old or older.
    private void Sweep(DateTimeOffset hour)
    {
        var last = Interlocked.Read(ref _sweptHourTicks);
        if (last >= hour.UtcTicks || Interlocked.CompareExchange(ref _sweptHourTicks, hour.UtcTicks, last) != last)
        {
            return;
        }

        var oldest = hour.AddHours(-1);
        foreach (var entry in _entries.Keys)
        {
            if (entry.Hour < oldest)
            {
                _entries.TryRemove(entry, out _);
            }
        }
    }
}
