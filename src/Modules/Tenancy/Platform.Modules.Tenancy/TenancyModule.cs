using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Data;

namespace Platform.Modules.Tenancy;

public static class TenancyModule
{
    internal const string DataSourceKey = "tenancy";
    private const int DefaultMaxPoolSize = 20;
    private static readonly string[] PoolSizeKeywords = ["Maximum Pool Size", "Max Pool Size", "MaxPoolSize"];

    public static IServiceCollection AddTenancyModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Every module owns a pool; the pools of all modules together must stay below PostgreSQL max_connections.
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var given = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (!PoolSizeKeywords.Any(given.ContainsKey))
        {
            builder.MaxPoolSize = DefaultMaxPoolSize;
        }

        var pooled = builder.ConnectionString;
        services.AddKeyedSingleton(DataSourceKey, (_, _) => NpgsqlDataSource.Create(pooled));
        services.AddSingleton<ITenantDirectory, TenantDirectory>();
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "tenancy", typeof(TenancyModule).Assembly, cancellationToken);
}
