using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Tenancy;

public static class TenancyModule
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton<TenancyModuleMarker>();
        return services;
    }
}

internal sealed class TenancyModuleMarker;
