using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Identity;

public static class IdentityModule
{
    /// <summary>Authenticated and a member of the host tenant's organization. The host uses it as the fallback policy.</summary>
    public static AuthorizationPolicy SameTenantPolicy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new SameTenantRequirement())
        .Build();

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IAuthorizationHandler, SameTenantHandler>();
        return services;
    }
}
