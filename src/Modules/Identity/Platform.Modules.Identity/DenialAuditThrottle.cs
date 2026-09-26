namespace Platform.Modules.Identity;

/// <summary>One kind of denial: the same tenant, audit action, user, host and path.</summary>
internal readonly record struct DenialKey(Guid TenantId, string Action, string? UserId, string Host, string Path);

/// <summary>
/// Lets an authorization denial be audited once per <see cref="DenialKey"/> per <see cref="Window"/> (spec 4.1; W-27). A
/// page can evaluate a policy several times per request and a user can retry, and neither should flood the tenant's audit
/// log. The memory is per process: with several web instances each writes at most one row per window. Expired entries
/// are dropped once more than <see cref="PruneAbove"/> are held, so a stream of distinct denials cannot grow it for ever.
/// </summary>
internal sealed class DenialAuditThrottle(TimeProvider clock)
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    internal const int PruneAbove = 10_000;

    private readonly Dictionary<DenialKey, DateTimeOffset> _lastAudited = [];
    private readonly Lock _gate = new();

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _lastAudited.Count;
            }
        }
    }

    /// <summary>True, and the window starts again, when this denial was not audited within the last minute.</summary>
    public bool ShouldAudit(DenialKey key)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_lastAudited.TryGetValue(key, out var at) && now - at < Window)
            {
                return false;
            }

            if (_lastAudited.Count > PruneAbove)
            {
                foreach (var expired in _lastAudited.Where(e => now - e.Value >= Window).Select(e => e.Key).ToList())
                {
                    _lastAudited.Remove(expired);
                }
            }

            _lastAudited[key] = now;
            return true;
        }
    }
}
