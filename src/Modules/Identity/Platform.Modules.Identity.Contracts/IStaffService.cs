using Platform.Shared.Results;

namespace Platform.Modules.Identity.Contracts;

/// <summary>What happened to the invitation email (spec D-4).</summary>
public enum InvitationEmail
{
    /// <summary>
    /// The person was emailed: the set-password and TOTP enrolment link (72 hours) when the account needs setup, or a
    /// short notice that the tenant added them when it does not. The page cannot tell the two apart (no account oracle).
    /// </summary>
    Sent,

    /// <summary>The member was saved but the email was not sent; the admin can resend it.</summary>
    Failed,
}

/// <summary>
/// The outcome of an invitation or a resend: the member as saved and what happened to the email. Whether the account
/// existed before is deliberately not part of it: another tenant's staff must not be discoverable by inviting them
/// (it is kept in the audit entry as <c>existing_account</c>).
/// </summary>
public sealed record Invitation(Member Member, InvitationEmail Email);

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
    /// row with <paramref name="roles"/>, and emails the person: Keycloak's setup link (password and TOTP, whichever the
    /// account lacks) that returns to the tenant's host, or, for an account with nothing to set up, a short notice in both
    /// languages that the tenant added them. Both answer the same. Audited as <c>identity.member_invited</c>. Refused when
    /// the email is already a member of the tenant (<c>identity.member_exists</c>), when the account cannot be invited
    /// (<c>identity.account_disabled</c>, audited as <c>identity.invitation_refused</c>), when no role or an unknown role
    /// is given, or when the email or name is not valid. When the member row cannot be saved, the organization
    /// membership this call added is removed again and <c>identity.invitation_failed</c> is returned.
    /// </summary>
    Task<Result<Invitation>> InviteAsync(
        string email, string displayName, IReadOnlyCollection<string> roles, string actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Emails a member who has not signed in yet again, as <see cref="InviteAsync"/> does (the setup link, or the notice
    /// for an account with nothing to set up); audited as <c>identity.invitation_resent</c>. Refused for an active member.
    /// </summary>
    Task<Result<Invitation>> ResendAsync(string userId, string actorId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the member's roles, as <see cref="IMemberDirectory.SetRolesAsync"/> (audited, last admin kept).</summary>
    Task<Result<Member>> SetRolesAsync(
        string userId, IReadOnlyCollection<string> roles, string actorId, CancellationToken cancellationToken = default);
}
