using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Platform.Modules.Identity.Contracts;

namespace Platform.Modules.Identity;

/// <summary>
/// The login's <c>acr</c> must be a whole number at least <see cref="Level"/> (D-2). Keycloak maps levels to acr values
/// through the realm's <c>acr.loa.map</c>, which the platform realm keeps numeric. A missing, non-numeric or lower value
/// fails, and so does any second acr claim below the level, so a principal cannot pass with one good value among others.
/// The requirement is its own handler, so the policy needs no service registration.
/// </summary>
internal sealed class MinimumAcrRequirement(int level) : AuthorizationHandler<MinimumAcrRequirement>, IAuthorizationRequirement
{
    public int Level { get; } = level;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MinimumAcrRequirement requirement)
    {
        if (IsMetBy(context.User, requirement.Level))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>The rule of the requirement on its own: at least one acr claim, and every one at <paramref name="level"/> or higher.</summary>
    internal static bool IsMetBy(ClaimsPrincipal user, int level)
    {
        var values = user.FindAll(IdentityClaims.Acr).Select(c => c.Value).ToList();
        return values.Count > 0 && values.All(v => MeetsLevel(v, level));
    }

    private static bool MeetsLevel(string value, int level) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var achieved) && achieved >= level;
}
