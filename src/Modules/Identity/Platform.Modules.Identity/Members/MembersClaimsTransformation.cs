using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity.Members;

/// <summary>
/// Adds the user's roles in the host tenant, from <c>identity.members</c>, as <see cref="IdentityClaims.Role"/> claims on
/// an identity of its own (<see cref="AuthenticationType"/>), after the same-tenant check (spec 4.1). The tenant policies
/// read roles from that identity only, and any identity of that type already on the principal is replaced, so a role can
/// only come from our table, never from a token or a cookie. A user in the organization with no member row gets no role.
/// No tenant (the platform host) or no signed-in user: the principal is returned unchanged. Scoped: the lookup runs once
/// per request or circuit, however often authentication is evaluated.
/// </summary>
internal sealed class MembersClaimsTransformation(ITenantAccessor tenants, MemberDirectory members) : IClaimsTransformation
{
    public const string AuthenticationType = "waslabid-members";

    private (string UserId, Guid TenantId, IReadOnlyList<string> Roles)? _resolved;

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var tenant = tenants.Current;
        var userId = principal.FindFirst(IdentityClaims.Subject)?.Value;
        if (tenant is null || principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId))
        {
            return principal;
        }

        var roles = OrganizationClaims.BelongsTo(principal, tenant) ? await RolesAsync(tenant.TenantId, userId, principal) : [];
        var transformed = new ClaimsPrincipal(principal.Identities
            .Where(i => i.AuthenticationType != AuthenticationType)
            .Select(i => i.Clone()));
        transformed.AddIdentity(new ClaimsIdentity(
            roles.Select(r => new Claim(IdentityClaims.Role, r)), AuthenticationType, IdentityClaims.Subject, IdentityClaims.Role));
        return transformed;
    }

    private async Task<IReadOnlyList<string>> RolesAsync(Guid tenantId, string userId, ClaimsPrincipal principal)
    {
        if (_resolved is { } done && done.UserId == userId && done.TenantId == tenantId)
        {
            return done.Roles;
        }

        var verified = string.Equals(principal.FindFirst(IdentityClaims.EmailVerified)?.Value, "true", StringComparison.OrdinalIgnoreCase);
        var email = verified ? principal.FindFirst(IdentityClaims.Email)?.Value : null;
        var roles = await members.SignInAsync(userId, email, CancellationToken.None);
        _resolved = (userId, tenantId, roles);
        return roles;
    }
}
