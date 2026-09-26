using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity.Members;

/// <summary>
/// Adds the user's roles in the host tenant, from <c>identity.members</c>, as <see cref="IdentityClaims.Role"/> claims on
/// an identity of its own (<see cref="AuthenticationType"/>), after the same-tenant check (spec 4.1). The tenant policies
/// read roles from that identity only, and any identity of that type already on the principal is replaced, so a role can
/// only come from our table, never from a token or a cookie. A user in the organization with no member row gets no role.
/// No tenant (the platform host), no signed-in user, or a static request (<see cref="StaticRequests"/>): the principal is
/// returned unchanged. The roles of a member row are reused for <see cref="MemberRolesCache.CacheFor"/> across requests
/// (<see cref="MemberRolesCache"/>, invalidated when the directory or the staff service changes the member); within one
/// request or circuit the lookup runs once however often authentication is evaluated.
/// </summary>
internal sealed class MembersClaimsTransformation(
    ITenantAccessor tenants, MemberDirectory members, MemberRolesCache cache, IHttpContextAccessor http) : IClaimsTransformation
{
    public const string AuthenticationType = "waslabid-members";

    private (string UserId, Guid TenantId, IReadOnlyList<string> Roles)? _resolved;

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var tenant = tenants.Current;
        var userId = principal.FindFirst(IdentityClaims.Subject)?.Value;
        if (tenant is null || principal.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId)
            || (http.HttpContext is { } context && StaticRequests.IsStatic(context.Request.Path)))
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

        if (!cache.TryGet(tenantId, userId, out var roles))
        {
            var generation = cache.Generation;
            var verified = string.Equals(principal.FindFirst(IdentityClaims.EmailVerified)?.Value, "true", StringComparison.OrdinalIgnoreCase);
            var email = verified ? principal.FindFirst(IdentityClaims.Email)?.Value : null;
            var found = await members.SignInAsync(userId, email, CancellationToken.None);
            if (found is not null)
            {
                cache.Set(tenantId, userId, found, generation);
            }

            roles = found ?? [];
        }

        _resolved = (userId, tenantId, roles);
        return roles;
    }
}
