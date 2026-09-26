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
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                });
            return Results.Redirect(IsLocal(returnUrl) ? returnUrl! : "/");
        }).AllowAnonymous();
        return app;
    }

    // Same-site paths only: "/x" yes; "//evil" and "/\evil" no (open-redirect protection).
    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
