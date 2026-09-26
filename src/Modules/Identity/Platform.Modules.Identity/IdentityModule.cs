using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IdentityModuleMarker>();
        return services;
    }
}

internal sealed class IdentityModuleMarker;
