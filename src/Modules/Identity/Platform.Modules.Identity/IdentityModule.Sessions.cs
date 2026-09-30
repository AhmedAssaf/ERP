using System.Security.Claims;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Members;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

/// <summary>
/// What an already authenticated principal is, by the same rules the policies apply, for code that must classify a session
/// without authorizing an endpoint: the usage metrics (W-10, spec 6.2) count staff, vendors and platform admins. These
/// read claims only; they never grant access, and the policies stay the gate.
/// </summary>
public static partial class IdentityModule
{
    /// <summary>
    /// Staff of the host tenant: signed in, a member of the tenant's Keycloak organization, not holding the realm role
    /// <c>vendor</c> (<see cref="IdentityModule.TenantStaffPolicy"/>), and holding at least one tenant role on the members
    /// identity (<see cref="MembersClaimsTransformation"/>), so a role claim on any other identity counts for nothing.
    /// </summary>
    public static bool IsTenantStaff(ClaimsPrincipal user, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(tenant);
        return user.Identity?.IsAuthenticated == true
            && !user.HasClaim(IdentityClaims.Roles, IdentityClaims.VendorRealmRole)
            && OrganizationClaims.BelongsTo(user, tenant)
            && user.Identities.Any(i => i.AuthenticationType == MembersClaimsTransformation.AuthenticationType && i.HasClaim(c => c.Type == IdentityClaims.Role));
    }

    /// <summary>
    /// A vendor user of the host tenant as the Vendor policy sees its claims: signed in, holding the realm role <c>vendor</c>,
    /// and a member of the tenant's organization (the join page's JoiningVendor policy does not require the membership).
    /// The company check of that policy is the caller's vendor context.
    /// </summary>
    public static bool IsTenantVendor(ClaimsPrincipal user, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(tenant);
        return user.Identity?.IsAuthenticated == true
            && user.HasClaim(IdentityClaims.Roles, IdentityClaims.VendorRealmRole)
            && OrganizationClaims.BelongsTo(user, tenant);
    }

    /// <summary>The claims of <see cref="PlatformAdminRequirements"/>: signed in, the platform-admin role, acr 2 or higher.</summary>
    public static bool IsPlatformAdmin(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Identity?.IsAuthenticated == true
            && user.HasClaim(IdentityClaims.Roles, PlatformAdminRole)
            && MinimumAcrRequirement.IsMetBy(user, PlatformMinimumAcr);
    }
}
