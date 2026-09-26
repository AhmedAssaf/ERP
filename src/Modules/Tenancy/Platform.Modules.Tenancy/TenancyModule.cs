using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Data;

namespace Platform.Modules.Tenancy;

public static class TenancyModule
{
    internal const string DataSourceKey = "tenancy";

    public static IServiceCollection AddTenancyModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddMemoryCache();
        services.AddKeyedSingleton(DataSourceKey, (_, _) => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<ITenantDirectory, TenantDirectory>();
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "tenancy", typeof(TenancyModule).Assembly, cancellationToken);
}
