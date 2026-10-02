namespace Platform.Modules.Vendors.RateLimiting;

/// <summary>
/// At most <c>limit</c> permits per key in any <c>window</c> (W-35, W-37): a sliding log, so a key never gets twice its
/// limit across a window boundary as a fixed window allows. It reads <see cref="TimeProvider"/>, so tests move time;
/// <c>System.Threading.RateLimiting</c>'s limiters run on their own timers and cannot. A refused attempt takes no permit.
/// <para>
/// Each key keeps the times of its last <c>limit</c> permits in a ring: a new permit is allowed while fewer are held or
/// the oldest has left the window, and then replaces it, so every step is O(1). At most <c>capacity</c> keys are
/// remembered, as <c>DuplicateCrThrottle</c> bounds its users: keys sit in a list from least to most recently used (an
/// attempt, permitted or refused, is a use), keys unused for a whole window are dropped from its old end, and when the
/// list is full the least recently used key is evicted (it starts again with its full limit).
/// </para>
/// The memory is per process: with several web instances each allows the limit (W-34 moves such limits to a shared store).
/// </summary>
internal sealed class SlidingWindowLimiter
{
    internal const int DefaultCapacity = 10_000;

    private readonly int _limit;
    private readonly long _windowTicks;
    private readonly int _capacity;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _byLastUse = new();
    private readonly Lock _gate = new();

    public SlidingWindowLimiter(int limit, TimeSpan window, TimeProvider clock, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentNullException.ThrowIfNull(clock);
        _limit = limit;
        _windowTicks = window.Ticks;
        _capacity = capacity;
        _clock = clock;
    }

    /// <summary>The keys remembered now.</summary>
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

    /// <summary>Takes one permit for <paramref name="key"/>: true when it had one left in the window ending now.</summary>
    public bool TryAcquire(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var now = _clock.GetUtcNow().UtcTicks;
        lock (_gate)
        {
            DropIdle(now);
            if (_index.TryGetValue(key, out var node))
            {
                _byLastUse.Remove(node);
                _byLastUse.AddLast(node);
            }
            else
            {
                while (_index.Count >= _capacity && _byLastUse.First is { } leastRecent)
                {
                    _byLastUse.RemoveFirst();
                    _index.Remove(leastRecent.Value.Key);
                }

                node = _byLastUse.AddLast(new Entry(key, _limit));
                _index[key] = node;
            }

            node.Value.LastUse = now;
            return node.Value.TryTake(now, _windowTicks);
        }
    }

    // Every permit of a key is at or before its last use, so a key unused for a whole window holds none in it.
    private void DropIdle(long now)
    {
        while (_byLastUse.First is { } leastRecent && now - leastRecent.Value.LastUse >= _windowTicks)
        {
            _byLastUse.RemoveFirst();
            _index.Remove(leastRecent.Value.Key);
        }
    }

    private sealed class Entry(string key, int limit)
    {
        private readonly long[] _permits = new long[limit];
        private int _oldest;
        private int _held;

        public string Key { get; } = key;

        public long LastUse { get; set; }

        public bool TryTake(long now, long windowTicks)
        {
            if (_held < _permits.Length)
            {
                _permits[(_oldest + _held) % _permits.Length] = now;
                _held++;
                return true;
            }

            if (now - _permits[_oldest] < windowTicks)
            {
                return false;
            }

            _permits[_oldest] = now;
            _oldest = (_oldest + 1) % _permits.Length;
            return true;
        }
    }
}
