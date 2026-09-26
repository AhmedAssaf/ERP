using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// Issues an authentication cookie exactly as a cookie scheme of the host would (its own ticket format and data
/// protection purpose), so a test can present a signed-in session to another host or under another cookie's name.
/// </summary>
internal static class AuthCookies
{
    public static string Protect(IServiceProvider services, string scheme, ClaimsPrincipal user)
    {
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddMinutes(30) };
        return options.TicketDataFormat.Protect(new AuthenticationTicket(user, properties, scheme));
    }

    public static ClaimsPrincipal Principal(IEnumerable<Claim> claims) =>
        new(new ClaimsIdentity(claims, "AuthenticationTypes.Federation", "preferred_username", "roles"));

    public static HttpRequestMessage WithCookie(this HttpRequestMessage request, string name, string value)
    {
        request.Headers.Add("Cookie", $"{name}={value}");
        return request;
    }
}
