using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Modules.Identity.Contracts;

namespace Platform.Modules.Identity;

public static class IdentityModule
{
    /// <summary>Authenticated and a member of the host tenant's organization. The host uses it as the fallback policy.</summary>
    public static AuthorizationPolicy SameTenantPolicy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new SameTenantRequirement())
        .Build();

    /// <summary>Realm role of the platform realm (<c>waslabid-platform</c>) that opens the platform console.</summary>
    public const string PlatformAdminRole = "platform-admin";

    /// <summary>The acr level of an OTP login in the platform realm (D-2); a password-only login is level 1.</summary>
    public const int PlatformMinimumAcr = 2;

    /// <summary>
    /// The requirements of the PlatformAdmin policy (spec 3.1, D-2), without an authentication scheme: the web host binds
    /// them to its platform cookie. Authenticated, holding the platform-admin realm role, and signed in at acr level 2 or
    /// higher, so a password-only session is refused even if the realm's flow drifts.
    /// </summary>
    public static AuthorizationPolicy PlatformAdminRequirements { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(IdentityClaims.Roles, PlatformAdminRole)
        .AddRequirements(new MinimumAcrRequirement(PlatformMinimumAcr))
        .Build();

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IAuthorizationHandler, SameTenantHandler>();
        return services;
    }
}
