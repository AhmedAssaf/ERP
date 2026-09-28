using Microsoft.AspNetCore.Authorization;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.PlatformHost;

namespace Platform.Web.Vendor;

/// <summary>
/// Where a signed-in principal holding the realm role <c>vendor</c> lands when it opens the tenant's home (<c>GET /</c>)
/// or the vendor home (<c>GET /vendor</c>) on a tenant host. A vendor whose company does not work with this tenant yet
/// (it passes the JoiningVendor policy but not the Vendor policy, so only the host check fails) goes to
/// <c>/vendor/join</c>, which offers "Work with {tenant}" (spec section 3), instead of an empty 403. Otherwise the tenant's
/// home, a staff page the tenant staff policy refuses to a vendor, sends it to the vendor home, where the Vendor policy
/// decides. Runs after authentication and before authorization; every other request, and every principal without the
/// vendor role, passes untouched without a policy evaluation.
/// </summary>
internal sealed class VendorHomeRedirectMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantAccessor tenants, IAuthorizationService authorization)
    {
        var path = context.Request.Path;
        var home = path == "/";
        if (tenants.Current is not null
            && !PlatformRequest.IsPlatform(context)
            && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && (home || path == VendorRegistrationEndpoints.HomePath)
            && context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(IdentityClaims.Roles, IdentityClaims.VendorRealmRole))
        {
            if (await NotJoinedYetAsync(context, authorization))
            {
                context.Response.Redirect(VendorJoinPath);
                return;
            }

            if (home)
            {
                context.Response.Redirect(VendorRegistrationEndpoints.HomePath);
                return;
            }
        }

        await next(context);
    }

    /// <summary>The join page on a tenant host.</summary>
    public const string VendorJoinPath = "/vendor/join";

    private static async Task<bool> NotJoinedYetAsync(HttpContext context, IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(context.User, context, VendorPolicies.JoiningVendor)).Succeeded
        && !(await authorization.AuthorizeAsync(context.User, context, VendorPolicies.Vendor)).Succeeded;
}
