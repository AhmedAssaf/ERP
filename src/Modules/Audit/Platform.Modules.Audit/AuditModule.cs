using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Audit;

public static class AuditModule
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton<AuditModuleMarker>();
        return services;
    }
}

internal sealed class AuditModuleMarker;
