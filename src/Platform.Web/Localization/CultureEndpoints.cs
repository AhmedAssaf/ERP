using Microsoft.AspNetCore.Localization;
using Platform.UI;

namespace Platform.Web.Localization;

internal static class CultureEndpoints
{
    public static IEndpointRouteBuilder MapCultureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/culture/set", (string culture, string? returnUrl, HttpContext context) =>
        {
            var normalized = PlatformLocalization.Normalize(culture);
            if (normalized is null)
            {
                return Results.BadRequest();
            }

            context.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(normalized)),
                new CookieOptions
                {
                    // Explicit: without it the cookie would still default to "/", but leaving it implicit invites a
                    // regression (e.g. a future relative Path) that would scope the cookie to /culture and it would
                    // never be sent back on "/".
                    Path = "/",
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    // The app runs behind Caddy over plain http; the auth cookie already uses CookieSecurePolicy.Always.
                    Secure = true,
                });
            return Results.Redirect(IsLocal(returnUrl) ? returnUrl! : "/");
        }).AllowAnonymous();
        return app;
    }

    // Same-site paths only: "/x" yes; "//evil", "/\evil" and anything with a control or whitespace
    // character no (open-redirect protection; browsers strip such characters before following a URL).
    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
}
