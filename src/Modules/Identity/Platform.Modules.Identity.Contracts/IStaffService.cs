using Platform.Shared.Results;

namespace Platform.Modules.Identity.Contracts;

/// <summary>What happened to the invitation email Keycloak sends (spec D-4).</summary>
public enum InvitationEmail
{
    /// <summary>Keycloak accepted the email with the set-password and TOTP enrolment link (72 hours).</summary>
    Sent,

    /// <summary>The account already has a password and a TOTP credential, so there is nothing to set up.</summary>
    NotNeeded,

    /// <summary>The member was saved but Keycloak did not send the email; the admin can resend it.</summary>
    Failed,
}

/// <summary>
/// The outcome of an invitation or a resend: the member as saved, what happened to the email, and whether the Keycloak
/// account existed before (the person is staff of another tenant or was invited earlier).
/// </summary>
public sealed record Invitation(Member Member, InvitationEmail Email, bool ExistingAccount);

/// <summary>
/// Staff administration for the current tenant (F-06 as narrowed by docs/05 row 3, spec D-4 and 4.2). Keycloak holds the
/// account, the password and the TOTP credential; <c>identity.members</c> holds the tenant's roles. Every change is
/// audited under <c>actorId</c>, the signed-in tenant admin's Keycloak <c>sub</c>.
/// </summary>
public interface IStaffService
{
    /// <summary>Every member of the current tenant, ordered by display name.</summary>
    Task<IReadOnlyList<Member>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the Keycloak user by email or creates one, adds them to the tenant's organization, saves an invited member
    /// row with <paramref name="roles"/>, and has Keycloak email the setup link (password and TOTP, whichever the account
    /// lacks) that returns to the tenant's host. Audited as <c>identity.member_invited</c>. Refused when the email is
    /// already a member of the tenant (<c>identity.member_exists</c>), when no role or an unknown role is given, or when
    /// the email or name is not valid.
    /// </summary>
    Task<Result<Invitation>> InviteAsync(
        string email, string displayName, IReadOnlyCollection<string> roles, string actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the setup link again to a member who has not signed in yet; audited as <c>identity.invitation_resent</c>.
    /// Refused for an active member and for an account that has nothing left to set up.
    /// </summary>
    Task<Result<Invitation>> ResendAsync(string userId, string actorId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the member's roles, as <see cref="IMemberDirectory.SetRolesAsync"/> (audited, last admin kept).</summary>
    Task<Result<Member>> SetRolesAsync(
        string userId, IReadOnlyCollection<string> roles, string actorId, CancellationToken cancellationToken = default);
}
