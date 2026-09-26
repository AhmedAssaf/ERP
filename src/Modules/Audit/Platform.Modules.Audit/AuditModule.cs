using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Audit.Contracts;
using Platform.Shared.Data;

namespace Platform.Modules.Audit;

public static class AuditModule
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddModuleDbContext<AuditDbContext>(connectionString);
        services.AddScoped<IAuditWriter, AuditWriter>();
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "audit", typeof(AuditModule).Assembly, cancellationToken);
}
