using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Data;

namespace Platform.Modules.Vendors;

/// <summary>
/// The Vendors module (vendor slice, ADR-0008): one vendor company across tenants, schema <c>vendor</c>. Platform-level
/// company rows sit under row-level security keyed on the vendor company; tenant relationships under the tenant policy.
/// </summary>
public static class VendorsModule
{
    public static IServiceCollection AddVendorsModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddModuleDbContext<VendorsDbContext>(connectionString);
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "vendors", typeof(VendorsModule).Assembly, cancellationToken);
}
