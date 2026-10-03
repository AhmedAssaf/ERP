using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// W-34: Redis answers <c>PING</c> through the host's multiplexer, so an outage is alerted by F-60 (the web hosts then
/// fall back to per-instance duplicate-CR limits). Unhealthy names the exception type only: never its message, which
/// can carry the endpoint, nor the connection string or its password (N-10). The job's per-check timeout cancels the wait.
/// </summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    private const string Component = "Redis";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach(Component)));
        }
    }
}
