using Platform.Shared.Results;

namespace Platform.Modules.Identity.Contracts;

public enum MemberStatus
{
    /// <summary>Invited (or seeded) and not signed in yet.</summary>
    Invited,

    /// <summary>Signed in at least once; the member row is bound to the Keycloak user.</summary>
    Active,
}

/// <summary>
/// A staff member of the current tenant (spec 4.1). <paramref name="UserId"/> is the Keycloak <c>sub</c>; it is null only
/// for a member row created before the user's first sign-in without a known Keycloak id (the development seed), which
/// is bound to the user by verified email on that sign-in.
/// </summary>
public sealed record Member(
    string? UserId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    MemberStatus Status,
    DateTimeOffset InvitedAt,
    DateTimeOffset? ActivatedAt);

/// <summary>
/// The current tenant's members and their roles (F-07, spec D-3 and 4.1), from <c>identity.members</c> under row-level
/// security. Every call works on the tenant of the current request or circuit.
/// </summary>
public interface IMemberDirectory
{
    /// <summary>The user's roles in the current tenant; empty when the user has no member row.</summary>
    Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Every member of the current tenant, ordered by display name.</summary>
    Task<IReadOnlyList<Member>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the member's roles and audits the change under <paramref name="actorId"/>. Refused, and audited, when it
    /// would leave the tenant without an active tenant admin (the last admin cannot drop their own admin role).
    /// </summary>
    Task<Result<Member>> SetRolesAsync(
        string userId, IReadOnlyCollection<string> roles, string actorId, CancellationToken cancellationToken = default);
}
