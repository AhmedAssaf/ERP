using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Platform.Modules.Identity.Contracts;

namespace Platform.Web.Account;

/// <summary>
/// W-21 on HTTP requests: the tenant cookie's events. At sign-in the cookie is stamped with the time Keycloak vouched for
/// the session (the token's <c>iat</c>, or the sign-in itself); on every request its principal is revalidated through
/// <see cref="IMembershipRevalidation"/> (the host tenant's organization when the session claims it, the account
/// otherwise), and a session that no longer stands is rejected and its cookie deleted, so the request goes on anonymous
/// and the authorization challenge sends the browser to Keycloak. The platform cookie does not use these events: its
/// realm has no organizations and its session lasts a fixed 15 minutes.
/// </summary>
internal static class MembershipRevalidation
{
    /// <summary>The authentication property holding the sign-in time, round-trip format, UTC.</summary>
    public const string SignedInAtItem = "waslabid.signed_in_at";

    /// <summary>The token's issue time claim, kept on the principal by the tenant scheme (its default claim actions drop it).</summary>
    public const string IssuedAtClaim = "iat";

    /// <summary>The cookie's sign-in event. A sliding renewal never raises it, so the stamp stays the real sign-in time.</summary>
    public static Task OnSigningIn(CookieSigningInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        StampSignIn(context.Properties, context.Principal is { } principal ? SignInTime(principal, now) : now);
        return Task.CompletedTask;
    }

    /// <summary>
    /// When Keycloak vouched for the organization in this sign-in: the token's <c>iat</c> (seconds before the cookie is
    /// written), never later than <paramref name="now"/>; <paramref name="now"/> when the token carries none.
    /// </summary>
    public static DateTimeOffset SignInTime(ClaimsPrincipal principal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return long.TryParse(principal.FindFirst(IssuedAtClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && DateTimeOffset.FromUnixTimeSeconds(seconds) is var issued && issued < now
                ? issued
                : now;
    }

    public static void StampSignIn(AuthenticationProperties properties, DateTimeOffset signedInAt)
    {
        ArgumentNullException.ThrowIfNull(properties);
        properties.Items[SignedInAtItem] = signedInAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    public static DateTimeOffset? SignedInAt(AuthenticationProperties properties) =>
        properties.Items.TryGetValue(SignedInAtItem, out var value)
        && DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;

    /// <summary>The cookie's principal validation, on every request that carries the tenant cookie.</summary>
    public static async Task OnValidatePrincipal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal is not { } principal)
        {
            return;
        }

        var revalidation = context.HttpContext.RequestServices.GetRequiredService<IMembershipRevalidation>();
        if (await revalidation.IsStillMemberAsync(principal, SignedInAt(context.Properties), context.HttpContext.RequestAborted))
        {
            return;
        }

        // The end of the session is already audited, or queued for the audit log, by the revalidation.
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
