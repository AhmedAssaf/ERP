namespace Platform.Modules.Vendors.Registration;

/// <summary>
/// A fixed window per key in process memory: the first <see cref="Record"/> of a key starts its window, and the key is
/// limited once it has <c>limit</c> records within <c>window</c> of that first one, until the window ends. The
/// duplicate-CR throttle (<see cref="DuplicateCrThrottle"/>) uses one per partition (account, source address) when no
/// Redis is configured or Redis does not answer.
/// <para>
/// At most <c>capacity</c> keys are remembered, as <c>DenialAuditThrottle</c> in the Identity module bounds its denials:
/// entries sit in a list ordered by the start of their window, with a dictionary pointing into it, so every step is O(1)
/// under the lock. Expired windows are dropped from the old end, and when the list is full the oldest entry is evicted
/// (that key starts a new window).
/// </para>
/// </summary>
internal sealed class InProcessWindowCounter(TimeProvider clock, int limit, TimeSpan window, int capacity)
{
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

    /// <summary>True when the key already has the limit of records in its current window.</summary>
    public bool IsLimited(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            DropExpired(clock.GetUtcNow());
            return _index.TryGetValue(key, out var node) && node.Value.Records >= limit;
        }
    }

    /// <summary>Counts one record for the key; the first one starts the key's window.</summary>
    public void Record(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            DropExpired(now);
            if (_index.TryGetValue(key, out var node))
            {
                node.Value.Records++;
                return;
            }

            while (_index.Count >= capacity && _byStart.First is { } oldest)
            {
                _byStart.RemoveFirst();
                _index.Remove(oldest.Value.Key);
            }

            _index[key] = _byStart.AddLast(new Entry(key, now));
        }
    }

    private void DropExpired(DateTimeOffset now)
    {
        while (_byStart.First is { } oldest && now - oldest.Value.Start >= window)
        {
            _byStart.RemoveFirst();
            _index.Remove(oldest.Value.Key);
        }
    }

    private sealed class Entry(string key, DateTimeOffset start)
    {
        public string Key { get; } = key;

        public DateTimeOffset Start { get; } = start;

        public int Records { get; set; } = 1;
    }
}
