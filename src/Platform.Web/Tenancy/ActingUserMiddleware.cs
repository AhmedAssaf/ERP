using System.Security.Claims;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Web.Tenancy;

/// <summary>
/// After authentication and before authorization and the endpoints: the request acts as its authenticated principal's
/// own <c>sub</c>, on tenant and platform hosts alike, so every database connection of the request carries
/// <c>app.user_id</c> (<c>platform.current_user_id()</c>). An anonymous request, or a principal without a subject, acts
/// as nobody. Nothing a client sends other than its authenticated cookie can name the user.
/// </summary>
internal sealed class ActingUserMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, ActingUserAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ActingUser.SetFrom(context.User, accessor);
        return next(context);
    }
}

/// <summary>The acting user of a principal, for requests and circuits.</summary>
internal static class ActingUser
{
    public static void SetFrom(ClaimsPrincipal? user, ActingUserAccessor accessor)
    {
        if (user?.Identity?.IsAuthenticated == true
            && user.FindFirst(IdentityClaims.Subject)?.Value is { } subject
            && !string.IsNullOrWhiteSpace(subject))
        {
            accessor.Set(subject);
        }
    }
}
