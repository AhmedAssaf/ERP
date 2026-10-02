using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Platform.Shared.Data;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// Spec 3.2: <c>select 1</c> as <c>erp_app</c>, over a new physical connection each time, from an unpooled data source named
/// <see cref="DataSourceNames.Health"/> (W-10: an unnamed one would be named after its connection string in metrics and spans).
/// </summary>
internal sealed class PostgreSqlHealthCheck(string connectionString) : IHealthCheck
{
    private const string Component = "PostgreSQL";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString) { Name = DataSourceNames.Health };
            builder.ConnectionStringBuilder.Pooling = false;
            await using var dataSource = builder.Build();
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("select 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10): a connection or authentication failure can echo the connection string.
            return HealthCheckResult.Unhealthy(
                HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach(Component)));
        }
    }
}
