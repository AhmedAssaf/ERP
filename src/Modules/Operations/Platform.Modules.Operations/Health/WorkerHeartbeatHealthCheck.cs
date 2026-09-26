using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>Spec 3.2: the Hangfire server heartbeat, self-checked from inside the worker that runs this job.</summary>
internal sealed class WorkerHeartbeatHealthCheck(JobStorage storage, TimeProvider timeProvider) : IHealthCheck
{
    private const string Component = "Worker";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;

            // JobStorage.GetMonitoringApi().Servers() is synchronous and does not observe a cancellation token; a
            // storage stuck on a dead connection would otherwise block past the caller's per-check timeout. Task.Run
            // moves the call to the thread pool and WaitAsync bounds how long this method waits for it, even though
            // the abandoned thread-pool work item keeps running until the call itself eventually returns.
            var servers = await Task.Run(() => storage.GetMonitoringApi().Servers(), cancellationToken)
                .WaitAsync(cancellationToken);
            var hasRecentHeartbeat = servers.Any(server => server.Heartbeat is { } heartbeat && now - heartbeat < StaleAfter);

            return hasRecentHeartbeat
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"{Component} has no recent heartbeat.");
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10): storage errors can carry the connection string. Also catches the
            // OperationCanceledException/TimeoutException from WaitAsync when the caller's timeout fires.
            return HealthCheckResult.Unhealthy(
                HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach(Component)));
        }
    }
}
