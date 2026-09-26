using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Platform.Shared.Tenancy;

namespace Platform.Shared.Data;

/// <summary>
/// Sets app.tenant_id on every connection EF Core opens so PostgreSQL row-level security sees the current tenant.
/// With no tenant it sets the empty string, which platform.current_tenant() reads as NULL: zero rows, inserts rejected.
/// Npgsql resets session state when the connection goes back to the pool.
/// </summary>
public sealed class TenantConnectionInterceptor(ITenantAccessor tenants) : DbConnectionInterceptor
{
    private const string SetTenantSql = "select set_config('app.tenant_id', @tenant, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
