using System.Security.Claims;

namespace Platform.Modules.Identity.Contracts;

/// <summary>
/// W-21: whether a signed-in session on a tenant host still stands, because its user is still an enabled member of that
/// tenant's Keycloak organization. The host asks on every request (the tenant cookie's principal validation) and every
/// minute in an open Blazor circuit; a false answer ends the session there.
/// </summary>
/// <remarks>
/// <para>
/// A session that claims the host tenant's organization is revalidated against it, staff and vendor alike: both get their
/// access to a tenant from membership of its organization (the SameTenant requirement inside the staff and Vendor
/// policies). Any other signed-in session on a tenant host (a vendor applicant, a vendor on another tenant's
/// <c>/vendor/join</c>, a cookie replayed on another host) is granted nothing by membership, but its account must still
/// exist and be enabled. The platform host has no tenant and is not revalidated here. A vendor removed from one tenant's
/// organization keeps its sessions on the other tenants' hosts (ADR-0008).
/// </para>
/// <para>
/// A removal, a disabled or a deleted account ends the session and is audited in the tenant's log as <c>identity.session_revoked</c>
/// naming the user. When Keycloak cannot answer, a membership confirmed within the grace period (by Keycloak or by a
/// sign-in) keeps the session; an older one ends it. Implementations cache, so this is not a Keycloak call per request.
/// </para>
/// </remarks>
public interface IMembershipRevalidation
{
    /// <param name="user">The session's principal.</param>
    /// <param name="signedInAt">
    /// When the session signed in through Keycloak (the token then carried the organization, which proves membership at
    /// that moment), or null when not known (a circuit's periodic check).
    /// </param>
    /// <param name="cancellationToken">Stops waiting; a Keycloak check already started for the same user completes for others.</param>
    Task<bool> IsStillMemberAsync(ClaimsPrincipal user, DateTimeOffset? signedInAt, CancellationToken cancellationToken = default);
}
