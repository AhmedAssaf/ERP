namespace Platform.Modules.Identity;

/// <summary>One kind of denial: the same tenant, audit action, user, host and path.</summary>
internal readonly record struct DenialKey(Guid TenantId, string Action, string? UserId, string Host, string Path);

/// <summary>
/// Lets an authorization denial be audited once per <see cref="DenialKey"/> per <see cref="Window"/> (spec 4.1; W-27). A
/// page can evaluate a policy several times per request and a user can retry, and neither should flood the tenant's audit
/// log. The memory is per process: with several web instances each writes at most one row per window.
/// <para>
/// At most <see cref="Capacity"/> denials are remembered. Entries sit in a list ordered by when they were last audited,
/// with a dictionary pointing into it, so every step is O(1) under the lock: expired entries are dropped from the old
/// end (each entry once in its life), and when the list is full the oldest entry is evicted. A flood of distinct denials
/// therefore costs memory up to the cap and no more; an evicted denial may be audited again inside its window.
/// </para>
/// </summary>
internal sealed class DenialAuditThrottle(TimeProvider clock)
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    internal const int Capacity = 10_000;

    private readonly Dictionary<DenialKey, LinkedListNode<(DenialKey Key, DateTimeOffset At)>> _index = [];
    private readonly LinkedList<(DenialKey Key, DateTimeOffset At)> _byAge = new();
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

    /// <summary>True, and the window starts again, when this denial was not audited within the last minute.</summary>
    public bool ShouldAudit(DenialKey key)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                if (now - node.Value.At < Window)
                {
                    return false;
                }

                _byAge.Remove(node);
                _index.Remove(key);
            }

            while (_byAge.First is { } oldest && (now - oldest.Value.At >= Window || _index.Count >= Capacity))
            {
                _byAge.RemoveFirst();
                _index.Remove(oldest.Value.Key);
            }

            _index[key] = _byAge.AddLast((key, now));
            return true;
        }
    }
}
