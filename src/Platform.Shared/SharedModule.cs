using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.Shared.Data;
using Platform.Shared.Tenancy;

namespace Platform.Shared;

public static class SharedModule
{
    public static IServiceCollection AddPlatformShared(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TenantAccessor>();
        services.AddScoped<ITenantAccessor>(sp => sp.GetRequiredService<TenantAccessor>());
        services.AddScoped<PlatformRequestContext>();
        services.AddScoped<IPlatformRequestContext>(sp => sp.GetRequiredService<PlatformRequestContext>());
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "platform", typeof(SharedModule).Assembly, cancellationToken);
}
