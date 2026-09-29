using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Diagnostics;
using Platform.Modules.Identity;
using Platform.Shared.Tenancy;

namespace Platform.Web.Account;

/// <summary>
/// W-21, QA D2 (decided by the user 2026-09-29): a signed-in user on a tenant host whose token does not carry that
/// tenant's organization (a removed staff member whom Keycloak's live session signs straight back in without it, a vendor
/// of another tenant, a cookie replayed on another host) sees <see cref="Path"/> instead of an empty 403: their access
/// to the tenant was removed, and a Sign out button that ends the local cookie and the Keycloak session.
/// <para>
/// The page is the body of the same 403 (status code pages re-execute the request at <see cref="Path"/>), never a
/// redirect or a challenge, so a browser is never sent anywhere, least of all into another tenant (pentest of the vendor
/// slice). Re-execution is off for every request (<see cref="UseAccessRemovedPage"/>) and switched on only by the tenant
/// cookie's forbid (<see cref="OnForbidden"/>) for a GET of a page by an authenticated principal without the host
/// tenant's organization. A refusal for a missing role, a refused Blazor connection, a refused post or an API call stays a
/// plain 403.
/// </para>
/// </summary>
internal static class AccessRemovedPage
{
    public const string Path = "/account/access-removed";

    /// <summary>The page's own policy: signed in, whatever the organization (the fallback policy would refuse it).</summary>
    public const string PolicyName = "SignedIn";

    public static AuthorizationPolicy Policy { get; } = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    /// <summary>
    /// Status code pages re-executing at <see cref="Path"/>, switched off for every request until
    /// <see cref="OnForbidden"/> switches it on. Goes after tenant resolution (the re-executed request keeps the tenant) and
    /// before authentication (the re-executed request is authorized for the page's own policy).
    /// </summary>
    public static WebApplication UseAccessRemovedPage(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseStatusCodePagesWithReExecute(Path);
        app.Use((context, next) =>
        {
            if (context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
            {
                statusCodePages.Enabled = false;
            }

            return next(context);
        });
        return app;
    }

    /// <summary>The tenant cookie's forbid: a 403, with the page when the refusal is for a missing organization.</summary>
    public static Task OnForbidden(RedirectContext<CookieAuthenticationOptions> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        if (HttpMethods.IsGet(http.Request.Method)
            && http.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null
            && http.User.Identity?.IsAuthenticated == true
            && http.RequestServices.GetService<ITenantAccessor>()?.Current is { } tenant
            && !IdentityModule.ClaimsOrganizationOf(http.User, tenant)
            && http.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
        {
            statusCodePages.Enabled = true;
        }

        return Task.CompletedTask;
    }
}
