using System.Collections.Concurrent;
using Platform.Modules.Identity.Contracts;

namespace Platform.Modules.Identity;

/// <summary>
/// Where member counts come from, uncached: the placeholder today, the Keycloak Admin API client from plan task 9, which
/// replaces <see cref="UnavailableOrganizationMembers"/> as this service. Null means unknown.
/// </summary>
internal interface IOrganizationMemberSource
{
    Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Identity module's <see cref="IOrganizationMembers"/>: a known count is reused for <see cref="CacheFor"/> per
/// organization, so each load of the console's tenant list does not ask Keycloak once per tenant. An unknown count is
/// not cached, so the next load tries again.
/// </summary>
internal sealed class CachingOrganizationMembers(IOrganizationMemberSource source, TimeProvider clock) : IOrganizationMembers
{
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset At)> _cache = new(StringComparer.Ordinal);

    public async Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationAlias);
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(organizationAlias, out var cached) && now - cached.At < CacheFor)
        {
            return cached.Count;
        }

        var count = await source.CountAsync(organizationAlias, cancellationToken);
        if (count is { } known)
        {
            _cache[organizationAlias] = (known, now);
        }

        return count;
    }
}
