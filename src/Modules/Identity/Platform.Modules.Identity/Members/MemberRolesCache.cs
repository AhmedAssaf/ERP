using System.Collections.Concurrent;

namespace Platform.Modules.Identity.Members;

/// <summary>
/// A member's roles per <c>(tenant, user)</c> for <see cref="CacheFor"/>, so the members claims transformation does not
/// read <c>identity.members</c> on every request (spec 4.1). Singleton, per process. <see cref="MemberDirectory"/> and
/// the staff service <see cref="Invalidate"/> a member they change, so a role change takes effect on that instance's next
/// request; another web instance picks it up when its entry expires, within <see cref="CacheFor"/>.
/// <para>
/// Only a member row that exists is cached: a user without one is looked up again, so a seeded row can still be bound by
/// verified email on a later request. A read that started before an invalidation is not stored (<see cref="Generation"/>),
/// so a lookup racing a role change cannot put the old roles back. Past <see cref="Capacity"/> entries the cache is
/// cleared rather than scanned.
/// </para>
/// </summary>
internal sealed class MemberRolesCache(TimeProvider clock)
{
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    internal const int Capacity = 50_000;

    private readonly ConcurrentDictionary<(Guid TenantId, string UserId), (IReadOnlyList<string> Roles, DateTimeOffset At)> _entries = new();
    private long _generation;

    /// <summary>Read before a lookup and passed to <see cref="Set"/>; it changes on every invalidation.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    public bool TryGet(Guid tenantId, string userId, out IReadOnlyList<string> roles)
    {
        if (_entries.TryGetValue((tenantId, userId), out var entry))
        {
            if (clock.GetUtcNow() - entry.At < CacheFor)
            {
                roles = entry.Roles;
                return true;
            }

            _entries.TryRemove(new KeyValuePair<(Guid, string), (IReadOnlyList<string>, DateTimeOffset)>((tenantId, userId), entry));
        }

        roles = [];
        return false;
    }

    /// <summary>Stores roles read after <paramref name="generation"/>; dropped if a member was invalidated since.</summary>
    public void Set(Guid tenantId, string userId, IReadOnlyList<string> roles, long generation)
    {
        if (_entries.Count >= Capacity)
        {
            _entries.Clear();
        }

        _entries[(tenantId, userId)] = (roles, clock.GetUtcNow());
        if (Generation != generation)
        {
            _entries.TryRemove((tenantId, userId), out _);
        }
    }

    public void Invalidate(Guid tenantId, string userId)
    {
        Interlocked.Increment(ref _generation);
        _entries.TryRemove((tenantId, userId), out _);
    }
}
