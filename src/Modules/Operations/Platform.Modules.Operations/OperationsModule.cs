using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Operations.Contracts;
using Platform.Shared.Data;

namespace Platform.Modules.Operations;

public static class OperationsModule
{
    public static IServiceCollection AddOperationsModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddModuleDbContext<OperationsDbContext>(connectionString);
        services.AddScoped<IHealthLog, HealthLog>();
        services.AddScoped<IPlatformAudit, PlatformAuditWriter>();
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "operations", typeof(OperationsModule).Assembly, cancellationToken);
}
