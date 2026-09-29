using System.Collections.Concurrent;

namespace Platform.Modules.Identity;

/// <summary>Keycloak's answer about one user in one organization (W-21).</summary>
internal enum OrganizationMembership
{
    /// <summary>An enabled member.</summary>
    Member,

    /// <summary>Not a member, the user no longer exists, or the organization no longer exists.</summary>
    NotMember,

    /// <summary>A member whose account is disabled.</summary>
    Disabled,

    /// <summary>Keycloak did not answer (unreachable, timed out, refused the service account, or not configured).</summary>
    Unavailable,
}

/// <summary>
/// Where membership answers come from, uncached: the Keycloak Admin API
/// (<see cref="Keycloak.KeycloakOrganizationMembershipSource"/>) when the host wires it, otherwise
/// <see cref="UnavailableOrganizationMembership"/>. Never throws for a Keycloak failure; that is
/// <see cref="OrganizationMembership.Unavailable"/>.
/// </summary>
internal interface IOrganizationMembershipSource
{
    Task<OrganizationMembership> CheckAsync(string organizationAlias, string userId, CancellationToken cancellationToken);
}

/// <summary>The source until <see cref="IdentityModule.AddKeycloakAdmin"/> replaces it: Keycloak never answers.</summary>
internal sealed class UnavailableOrganizationMembership : IOrganizationMembershipSource
{
    public Task<OrganizationMembership> CheckAsync(string organizationAlias, string userId, CancellationToken cancellationToken) =>
        Task.FromResult(OrganizationMembership.Unavailable);
}

/// <summary>What is known about a user's membership, and since when: confirmed, or seen removed.</summary>
internal readonly record struct MembershipFact(bool Member, DateTimeOffset At);

/// <summary>
/// The membership facts the web host has learned, per organization and user, shared by every request and circuit of the
/// process (singleton; W-21). A fact is replaced only by a newer one, so a sign-in older than a seen removal cannot mask
/// it, and a sign-in after it (Keycloak put the organization in the new token) supersedes it. Also holds the Keycloak
/// checks in flight, so concurrent requests of one user ask once, and the time until which Keycloak is not asked after a
/// failure. Past <see cref="Capacity"/> facts the map is cleared rather than scanned; the next check of each user asks
/// Keycloak again. Per process: with several web instances each learns on its own.
/// </summary>
internal sealed class MembershipEvidence
{
    internal const int Capacity = 50_000;

    private readonly ConcurrentDictionary<(string Alias, string UserId), MembershipFact> _facts = new();
    private readonly ConcurrentDictionary<(string Alias, string UserId), Lazy<Task<OrganizationMembership>>> _inFlight = new();
    private long _keycloakQuietUntilTicks;

    public MembershipFact? Latest(string alias, string userId) =>
        _facts.TryGetValue((alias, userId), out var fact) ? fact : null;

    /// <summary>Keeps <paramref name="fact"/> unless a fact at least as new is already known.</summary>
    public void Record(string alias, string userId, MembershipFact fact)
    {
        if (_facts.Count >= Capacity)
        {
            _facts.Clear();
        }

        _facts.AddOrUpdate((alias, userId), fact, (_, known) => fact.At >= known.At ? fact : known);
    }

    /// <summary>Drops the fact when it is still exactly <paramref name="fact"/> (a removal whose audit failed).</summary>
    public void Forget(string alias, string userId, MembershipFact fact) =>
        _facts.TryRemove(new KeyValuePair<(string, string), MembershipFact>((alias, userId), fact));

    /// <summary>
    /// Runs <paramref name="check"/> once for concurrent callers about the same user and organization; each caller waits
    /// for the same answer. The check itself records and audits what it learns, so it completes even when the caller that
    /// started it stops waiting.
    /// </summary>
    public Task<OrganizationMembership> CheckOnceAsync(
        string alias, string userId, Func<Task<OrganizationMembership>> check, CancellationToken cancellationToken)
    {
        var key = (alias, userId);
        var mine = new Lazy<Task<OrganizationMembership>>(check, LazyThreadSafetyMode.ExecutionAndPublication);
        var running = _inFlight.GetOrAdd(key, mine);
        if (ReferenceEquals(running, mine))
        {
            _ = mine.Value.ContinueWith(
                _ => _inFlight.TryRemove(new KeyValuePair<(string, string), Lazy<Task<OrganizationMembership>>>(key, mine)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return running.Value.WaitAsync(cancellationToken);
    }

    public bool KeycloakQuiet(DateTimeOffset now) => now.UtcTicks < Interlocked.Read(ref _keycloakQuietUntilTicks);

    public void QuietKeycloakUntil(DateTimeOffset until) => Interlocked.Exchange(ref _keycloakQuietUntilTicks, until.UtcTicks);
}
