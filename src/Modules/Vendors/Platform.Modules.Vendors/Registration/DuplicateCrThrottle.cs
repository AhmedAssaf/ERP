namespace Platform.Modules.Vendors.Registration;

/// <summary>
/// Limits how often one user can be told that a CR number is already on the platform (V-6): after <see cref="Limit"/>
/// refusals within <see cref="Window"/> of the first, the user is limited until that window ends, and the registration
/// then answers every CR number with the same neutral message, so the form cannot be used to test which numbers are
/// taken. The memory is per process: with several web instances each allows the limit.
/// <para>
/// At most <see cref="Capacity"/> users are remembered, as <c>DenialAuditThrottle</c> in the Identity module bounds its
/// denials: entries sit in a list ordered by the start of their window, with a dictionary pointing into it, so every step
/// is O(1) under the lock. Expired windows are dropped from the old end, and when the list is full the oldest entry is
/// evicted (that user starts a new window).
/// </para>
/// </summary>
internal sealed class DuplicateCrThrottle(TimeProvider clock)
{
    internal static readonly TimeSpan Window = TimeSpan.FromHours(1);
    internal const int Limit = 5;
    internal const int Capacity = 10_000;

    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _byStart = new();
    private readonly Lock _gate = new();

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>True when the user already had <see cref="Limit"/> duplicate refusals in the current window.</summary>
    public bool IsLimited(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        lock (_gate)
        {
            DropExpired(clock.GetUtcNow());
            return _index.TryGetValue(userId, out var node) && node.Value.Refusals >= Limit;
        }
    }

    /// <summary>Counts one duplicate refusal for the user; the first one starts the user's window.</summary>
    public void Record(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            DropExpired(now);
            if (_index.TryGetValue(userId, out var node))
            {
                node.Value.Refusals++;
                return;
            }

            while (_index.Count >= Capacity && _byStart.First is { } oldest)
            {
                _byStart.RemoveFirst();
                _index.Remove(oldest.Value.UserId);
            }

            _index[userId] = _byStart.AddLast(new Entry(userId, now));
        }
    }

    private void DropExpired(DateTimeOffset now)
    {
        while (_byStart.First is { } oldest && now - oldest.Value.Start >= Window)
        {
            _byStart.RemoveFirst();
            _index.Remove(oldest.Value.UserId);
        }
    }

    private sealed class Entry(string userId, DateTimeOffset start)
    {
        public string UserId { get; } = userId;

        public DateTimeOffset Start { get; } = start;

        public int Refusals { get; set; } = 1;
    }
}
