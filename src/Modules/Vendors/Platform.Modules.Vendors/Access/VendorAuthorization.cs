using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;

namespace Platform.Modules.Vendors.Access;

/// <summary>The signed-in user has a <c>vendor.vendor_users</c> row (spec section 3, Vendor policy).</summary>
internal sealed class VendorCompanyRequirement : IAuthorizationRequirement;

/// <summary>
/// Passes when <c>vendor.company_of_user</c> finds a company for the principal's own <c>sub</c>. It never reads a user id
/// from anywhere else, and it never sets the vendor context: the host does that only after the whole Vendor policy passed.
/// </summary>
internal sealed class VendorCompanyHandler(IVendorUsers users, IHttpContextAccessor http) : AuthorizationHandler<VendorCompanyRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, VendorCompanyRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || context.User.FindFirst(IdentityClaims.Subject)?.Value is not { Length: > 0 } userId)
        {
            return;
        }

        var aborted = (context.Resource as HttpContext ?? http.HttpContext)?.RequestAborted ?? CancellationToken.None;
        if (await users.FindCompanyAsync(userId, aborted) is not null)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Claims the vendor policies read, compared as Keycloak issues them.</summary>
internal static class VendorClaims
{
    /// <summary>Keycloak verified the address (<c>email_verified</c> is the JSON boolean true).</summary>
    public static bool EmailVerified(AuthorizationHandlerContext context) =>
        context.User.FindAll(IdentityClaims.EmailVerified).Any(c => string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase));
}
