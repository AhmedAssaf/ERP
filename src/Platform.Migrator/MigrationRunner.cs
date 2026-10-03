using Npgsql;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;
using Platform.Modules.Vendors;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Shared.Jobs;

namespace Platform.Migrator;

/// <summary>Applies every module's SQL migrations as the owner role, in dependency order, and installs Hangfire's tables.</summary>
public static class MigrationRunner
{
    public static async Task<IReadOnlyList<string>> RunAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        var applied = new List<string>();
        applied.AddRange(Named("platform", await SharedModule.MigrateAsync(connection, cancellationToken)));
        // W-36: Hangfire's tables, installed or upgraded as the owner after platform 0008 set their grants; on a connection of
        // its own, since Hangfire's scripts change the search path.
        await using (var hangfire = new NpgsqlConnection(ownerConnectionString))
        {
            await hangfire.OpenAsync(cancellationToken);
            JobsModule.InstallSchema(hangfire);
        }

        applied.AddRange(Named("audit", await AuditModule.MigrateAsync(connection, cancellationToken)));
        applied.AddRange(Named("tenancy", await TenancyModule.MigrateAsync(connection, cancellationToken)));
        applied.AddRange(Named("identity", await IdentityModule.MigrateAsync(connection, cancellationToken)));
        applied.AddRange(Named("workflow", await WorkflowModule.MigrateAsync(connection, cancellationToken)));
        applied.AddRange(Named("operations", await OperationsModule.MigrateAsync(connection, cancellationToken)));
        applied.AddRange(Named("vendors", await VendorsModule.MigrateAsync(connection, cancellationToken)));
        return applied;
    }

    private static IEnumerable<string> Named(string module, IEnumerable<string> scripts) => scripts.Select(s => $"{module}/{s}");
}
