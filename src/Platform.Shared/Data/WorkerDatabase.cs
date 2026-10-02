using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Platform.Shared.Data;

/// <summary>
/// W-36 (ADR-0012 addendum 2026-10-03): the worker's own database role and connection string. The worker connects only as
/// <see cref="RoleName"/>, which holds the worker-only rights (the worker functions, the ops writes) on top of the
/// application role's; the web host never connects as it. Messages name the setting and the role, never a value (N-10).
/// </summary>
public static class WorkerDatabase
{
    public const string RoleName = "erp_worker";

    public const string ConnectionStringName = "Worker";

    /// <summary>The worker's connection string; refuses a missing one and one for any other role.</summary>
    public static string ConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var worker = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(worker))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured (ConnectionStrings:{ConnectionStringName}). The worker connects "
                + $"only as its own role, {RoleName}; set it with dotnet user-secrets (docs/07 section 4).");
        }

        // Exactly the role the migrator gives a login (WorkerRole): never the application role, whose sessions must not hold
        // the worker's rights, and never a pasted owner or other connection string with more rights.
        if (!string.Equals(new NpgsqlConnectionStringBuilder(worker).Username, RoleName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' must connect as the worker's own role ({RoleName}), not as the application role or any other role.");
        }

        return worker;
    }

    /// <summary>Refuses a host connection string (<paramref name="name"/>) that connects as the worker's role.</summary>
    public static void RefuseWorkerRole(string connectionString, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (string.Equals(new NpgsqlConnectionStringBuilder(connectionString).Username, RoleName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Connection string '{name}' (ConnectionStrings:{name}) must not connect as {RoleName}: the worker's rights stay out of the web host.");
        }
    }
}
