using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Platform.Modules.Operations.Health;

/// <summary>Spec 3.2: <c>select 1</c> as <c>erp_app</c>.</summary>
internal sealed class PostgreSqlHealthCheck(string connectionString) : IHealthCheck
{
    private const string Component = "PostgreSQL";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
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
