using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Platform.Web.PlatformHost;

namespace Platform.Web.Account;

/// <summary>
/// Sign-out on both kinds of host (spec 3.1). Each is a POST with an antiforgery token (AppShell's user menu posts it):
/// it deletes this host's cookie only, then sends the browser to its own realm's end-session endpoint, which returns to
/// the scheme's signed-out callback and from there to the host's home. The other host's cookie is never touched.
/// <see cref="PlatformHostMiddleware"/> keeps each path on its own kind of host (404 elsewhere). The endpoints are
/// anonymous so an expired session can still sign out; the antiforgery token binds the POST to the signed-in user.
/// </summary>
internal static class SignOutEndpoints
{
    public const string TenantPath = "/account/sign-out";

    public static IEndpointRouteBuilder MapSignOutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(TenantPath, (HttpContext context, IAntiforgery antiforgery) => SignOutAsync(
                context, antiforgery, CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme, "/"))
            .AllowAnonymous();
        app.MapPost(PlatformAuthentication.SignOutPath, (HttpContext context, IAntiforgery antiforgery) => SignOutAsync(
                context, antiforgery, PlatformAuthentication.CookieScheme, PlatformAuthentication.OidcScheme, PlatformAuthentication.HomePath))
            .AllowAnonymous();
        return app;
    }

    /// <summary>
    /// The handlers keep no id token (<c>SaveTokens = false</c>), so the end-session request names the client instead;
    /// Keycloak needs one of the two to accept <c>post_logout_redirect_uri</c>, and then asks the user to confirm.
    /// </summary>
    public static Task NameClientOnEndSession(RedirectContext context)
    {
        context.ProtocolMessage.ClientId = context.Options.ClientId;
        return Task.CompletedTask;
    }

    private static async Task<IResult> SignOutAsync(
        HttpContext context, IAntiforgery antiforgery, string cookieScheme, string oidcScheme, string home)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        return Results.SignOut(new AuthenticationProperties { RedirectUri = home }, [cookieScheme, oidcScheme]);
    }
}
