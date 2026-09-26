using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.PlatformHost;

namespace Platform.Web.Vendor;

/// <summary>
/// A signed-in principal holding the realm role <c>vendor</c> who opens the tenant's home (<c>GET /</c>) is sent to the
/// vendor home. The home is a staff page, which the tenant staff policy refuses to a vendor with 403; the redirect gives
/// the vendor its own page instead, where the Vendor policy decides. Runs after authentication and before authorization,
/// on tenant hosts only; every other request passes untouched.
/// </summary>
internal sealed class VendorHomeRedirectMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, ITenantAccessor tenants)
    {
        if (tenants.Current is not null
            && !PlatformRequest.IsPlatform(context)
            && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && context.Request.Path == "/"
            && context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(IdentityClaims.Roles, IdentityClaims.VendorRealmRole))
        {
            context.Response.Redirect(VendorRegistrationEndpoints.HomePath);
            return Task.CompletedTask;
        }

        return next(context);
    }
}
