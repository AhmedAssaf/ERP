using System.Collections.Concurrent;

namespace Platform.Modules.Identity;

/// <summary>Keycloak's answer about one user in one organization, or about the account itself (W-21).</summary>
internal enum OrganizationMembership
{
    /// <summary>An enabled member (or, for an account check, an enabled account).</summary>
    Member,

    /// <summary>Not a member, the user no longer exists, or the organization no longer exists.</summary>
    NotMember,

    /// <summary>A member whose account is disabled (or, for an account check, a disabled account).</summary>
    Disabled,

    /// <summary>Keycloak did not answer (unreachable, timed out, refused the service account, sent something unreadable, or not configured).</summary>
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
    /// <summary>Whether the user is an enabled member of the organization with <paramref name="organizationAlias"/>.</summary>
    Task<OrganizationMembership> CheckAsync(string organizationAlias, string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the account exists and is enabled, for a session that claims no organization of its host (P-2). A source
    /// that cannot tell answers <see cref="OrganizationMembership.Unavailable"/>.
    /// </summary>
    Task<OrganizationMembership> CheckAccountAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult(OrganizationMembership.Unavailable);
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
/// The membership facts the web host has learned, per scope (an organization alias, or <see cref="AccountScope"/> for the
/// account itself) and user, shared by every request and circuit of the process (singleton; W-21). A fact is replaced
/// only by a newer one, so a sign-in older than a seen removal cannot mask it, and a sign-in after it (Keycloak put the
/// organization in the new token) supersedes it. Also holds the Keycloak checks in flight, so concurrent requests of one
/// user ask once, and per scope the time until which Keycloak is not asked after a failure, so one organization's (or the
/// account endpoint's) failures never stop the checks of another. Past <see cref="Capacity"/> facts, confirmed
/// memberships are dropped first (the next check of those users asks Keycloak again) and seen removals only if nothing
/// else is left. Per process: with several web instances each learns on its own.
/// </summary>
internal sealed class MembershipEvidence
{
    /// <summary>The scope of an account check: no organization alias is empty.</summary>
    public const string AccountScope = "";

    internal const int DefaultCapacity = 50_000;

    private readonly ConcurrentDictionary<(string Scope, string UserId), MembershipFact> _facts = new();
    private readonly ConcurrentDictionary<(string Scope, string UserId), Lazy<Task<OrganizationMembership>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, long> _quietUntilTicks = new(StringComparer.Ordinal);
    private readonly Lock _trimGate = new();
    private readonly int _capacity;
    private long _count;

    public MembershipEvidence()
        : this(DefaultCapacity)
    {
    }

    internal MembershipEvidence(int capacity) => _capacity = capacity;

    /// <summary>The number of facts held (approximate under concurrent writes).</summary>
    internal long Count => Interlocked.Read(ref _count);

    public MembershipFact? Latest(string scope, string userId) =>
        _facts.TryGetValue((scope, userId), out var fact) ? fact : null;

    /// <summary>Keeps <paramref name="fact"/> unless a fact at least as new is already known.</summary>
    public void Record(string scope, string userId, MembershipFact fact)
    {
        var key = (scope, userId);
        while (true)
        {
            if (_facts.TryGetValue(key, out var known))
            {
                if (fact.At < known.At || _facts.TryUpdate(key, fact, known))
                {
                    return;
                }

                continue;
            }

            if (Count >= _capacity)
            {
                Trim();
            }

            if (_facts.TryAdd(key, fact))
            {
                Interlocked.Increment(ref _count);
                return;
            }
        }
    }

    /// <summary>Drops the fact when it is still exactly <paramref name="fact"/>.</summary>
    public void Forget(string scope, string userId, MembershipFact fact)
    {
        if (_facts.TryRemove(new KeyValuePair<(string, string), MembershipFact>((scope, userId), fact)))
        {
            Interlocked.Decrement(ref _count);
        }
    }

    /// <summary>
    /// Runs <paramref name="check"/> once for concurrent callers about the same user and scope; each caller waits for the
    /// same answer. The check itself records and audits what it learns, so it completes even when the caller that
    /// started it stops waiting.
    /// </summary>
    public Task<OrganizationMembership> CheckOnceAsync(
        string scope, string userId, Func<Task<OrganizationMembership>> check, CancellationToken cancellationToken)
    {
        var key = (scope, userId);
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

    public bool KeycloakQuiet(string scope, DateTimeOffset now) =>
        _quietUntilTicks.TryGetValue(scope, out var until) && now.UtcTicks < until;

    public void QuietKeycloakUntil(string scope, DateTimeOffset until) => _quietUntilTicks[scope] = until.UtcTicks;

    // Confirmed memberships go first: forgetting one costs one Keycloak call; forgetting a seen removal costs a call for
    // every replay of the ended session and a second audit row for the same removal.
    private void Trim()
    {
        lock (_trimGate)
        {
            if (Count < _capacity)
            {
                return;
            }

            foreach (var entry in _facts.Where(e => e.Value.Member))
            {
                Forget(entry.Key.Scope, entry.Key.UserId, entry.Value);
            }

            if (Count < _capacity)
            {
                return;
            }

            foreach (var entry in _facts)
            {
                Forget(entry.Key.Scope, entry.Key.UserId, entry.Value);
            }
        }
    }
}
