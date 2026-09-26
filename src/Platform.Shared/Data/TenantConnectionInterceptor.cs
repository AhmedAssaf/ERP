using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Platform.Shared.Tenancy;

namespace Platform.Shared.Data;

/// <summary>
/// Sets app.tenant_id on every connection EF Core opens so PostgreSQL row-level security sees the current tenant.
/// With no tenant it sets the empty string, which platform.current_tenant() reads as NULL: zero rows, inserts rejected.
/// Npgsql resets session state when the connection goes back to the pool.
/// The first open per connection string checks the role: a superuser or BYPASSRLS role would silently ignore every
/// policy, so such a connection is refused instead of used.
/// </summary>
public sealed class TenantConnectionInterceptor(ITenantAccessor tenants) : DbConnectionInterceptor
{
    private const string SetTenantSql = "select set_config('app.tenant_id', @tenant, false)";
    private const string BypassesRlsSql = "select rolsuper or rolbypassrls from pg_roles where rolname = current_user";

    // Verdict per connection string: true when its role bypasses row-level security.
    private static readonly ConcurrentDictionary<string, bool> BypassVerdicts = new(StringComparer.Ordinal);

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!BypassVerdicts.TryGetValue(connection.ConnectionString, out var bypasses))
        {
            using var check = CreateBypassCheck(connection);
            bypasses = BypassVerdicts.GetOrAdd(connection.ConnectionString, (bool)check.ExecuteScalar()!);
        }

        ThrowIfBypasses(bypasses);
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!BypassVerdicts.TryGetValue(connection.ConnectionString, out var bypasses))
        {
            await using var check = CreateBypassCheck(connection);
            bypasses = BypassVerdicts.GetOrAdd(connection.ConnectionString, (bool)(await check.ExecuteScalarAsync(cancellationToken))!);
        }

        ThrowIfBypasses(bypasses);
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ThrowIfBypasses(bool bypasses)
    {
        if (bypasses)
        {
            throw new InvalidOperationException(
                "The application connection uses a PostgreSQL role that bypasses row-level security (superuser or BYPASSRLS). "
                + "Connect as the application role (erp_app), not the owner.");
        }
    }

    private static DbCommand CreateBypassCheck(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = BypassesRlsSql;
        return command;
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = SetTenantSql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenants.Current?.TenantId.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty;
        command.Parameters.Add(parameter);
        return command;
    }
}
