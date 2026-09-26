using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>Spec 3.2: the Hangfire server heartbeat, self-checked from inside the worker that runs this job.</summary>
internal sealed class WorkerHeartbeatHealthCheck(JobStorage storage, TimeProvider timeProvider) : IHealthCheck
{
    private const string Component = "Worker";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var hasRecentHeartbeat = storage.GetMonitoringApi().Servers()
                .Any(server => server.Heartbeat is { } heartbeat && now - heartbeat < StaleAfter);

            return Task.FromResult(hasRecentHeartbeat
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"{Component} has no recent heartbeat."));
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10): storage errors can carry the connection string.
            return Task.FromResult(HealthCheckResult.Unhealthy(
                HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach(Component))));
        }
    }
}
