using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Web.Account;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// Issues an authentication cookie exactly as a cookie scheme of the host would (its own ticket format and data
/// protection purpose), so a test can present a signed-in session to another host or under another cookie's name.
/// </summary>
internal static class AuthCookies
{
    /// <summary>
    /// A cookie signed in now, stamped with the sign-in time as the tenant cookie's sign-in event stamps it (W-21), so the
    /// host counts it as membership confirmed at <paramref name="signedInAt"/> (default: now).
    /// </summary>
    public static string Protect(IServiceProvider services, string scheme, ClaimsPrincipal user, DateTimeOffset? signedInAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddMinutes(30) };
        MembershipRevalidation.StampSignIn(properties, signedInAt ?? now);
        return Protect(services, scheme, user, properties);
    }

    /// <summary>A cookie without the sign-in stamp: one issued before W-21, or by a scheme that does not stamp it.</summary>
    public static string ProtectWithoutSignInStamp(IServiceProvider services, string scheme, ClaimsPrincipal user)
    {
        var now = DateTimeOffset.UtcNow;
        return Protect(services, scheme, user, new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddMinutes(30) });
    }

    private static string Protect(IServiceProvider services, string scheme, ClaimsPrincipal user, AuthenticationProperties properties)
    {
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
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
