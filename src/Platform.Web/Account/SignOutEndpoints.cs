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
/// On a tenant host the form may name where to land afterwards (<c>returnUrl</c>, a local path such as <c>/vendor</c>
/// after a vendor registered, so the next sign-in carries the new role); anything that is not a local path is ignored.
/// </summary>
internal static class SignOutEndpoints
{
    public const string TenantPath = "/account/sign-out";

    public static IEndpointRouteBuilder MapSignOutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(TenantPath, async (HttpContext context, IAntiforgery antiforgery) => await SignOutAsync(
                context, antiforgery, CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme,
                await LocalReturnUrlAsync(context) ?? "/"))
            .AllowAnonymous();
        app.MapPost(PlatformAuthentication.SignOutPath, (HttpContext context, IAntiforgery antiforgery) => SignOutAsync(
                context, antiforgery, PlatformAuthentication.CookieScheme, PlatformAuthentication.OidcScheme, PlatformAuthentication.HomePath))
            .AllowAnonymous();
        return app;
    }

    /// <summary>
    /// The end-session request always names the client. The tenant scheme also keeps the id token (<see cref="KeepIdToken"/>),
    /// which the handler sends as <c>id_token_hint</c>, so Keycloak ends its session without asking; the platform scheme
    /// keeps none, so Keycloak asks the platform admin to confirm.
    /// </summary>
    public static Task NameClientOnEndSession(RedirectContext context)
    {
        context.ProtocolMessage.ClientId = context.Options.ClientId;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The tenant scheme's token-validated event (W-21, QA D2): keeps the id token, and only the id token, in the sign-in's
    /// properties, so the cookie carries it and the OpenID Connect handler's sign-out sends it as <c>id_token_hint</c>.
    /// With the hint Keycloak ends its own session without asking, so a removed user who signs out is shown the sign-in
    /// page next instead of being signed straight back in by that session. The access token is never stored.
    /// </summary>
    public static Task KeepIdToken(Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if ((context.TokenEndpointResponse?.IdToken ?? context.ProtocolMessage?.IdToken) is { Length: > 0 } idToken
            && context.Properties is { } properties)
        {
            properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = idToken }]);
        }

        return Task.CompletedTask;
    }

    /// <summary>The form's <c>returnUrl</c> when it is a local path ("/x", never "//host" or "/\host"); null otherwise.</summary>
    private static async Task<string?> LocalReturnUrlAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
        {
            return null;
        }

        var value = (await context.Request.ReadFormAsync(context.RequestAborted))["returnUrl"].ToString();
        return value.Length > 1 && value[0] == '/' && value[1] is not ('/' or '\\') && !value.Any(char.IsControl) ? value : null;
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
