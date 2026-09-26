using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Platform.Modules.Operations.Alerts;
using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// The recurring job "health-check" (plan task 3): runs every registered check with its own five-second timeout and
/// records one result per component through <see cref="IHealthLog"/>. A check that throws unexpectedly (it is
/// expected to catch its own failures) does not take the rest of the run down with it. Plan task 4 (F-60): the
/// incident transitions <see cref="IHealthLog.RecordAsync"/> returns are then handed to <see cref="IncidentNotifier"/>
/// so an opened or closed incident sends its one email.
/// </summary>
internal sealed class HealthCheckJob(
    IEnumerable<NamedHealthCheck> checks, IHealthLog healthLog, IncidentNotifier notifier, TimeProvider timeProvider)
{
    private static readonly TimeSpan PerCheckTimeout = TimeSpan.FromSeconds(5);

    // Task 3 (no overlapping check runs, no lost cycle): the recurring trigger fires every minute regardless of
    // whether the previous run finished; DisableConcurrentExecution refuses a second run while one is still going,
    // and AutomaticRetry(Attempts = 0) means a run that fails outright is not retried - a missed minute is simply
    // replaced by the next scheduled run rather than piling up retries behind it.
    [DisableConcurrentExecution(timeoutInSeconds: 50)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<HealthResult>();

        foreach (var named in checks)
        {
            var checkedAt = timeProvider.GetUtcNow();
            var startedAt = timeProvider.GetTimestamp();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PerCheckTimeout);

            HealthCheckResult result;
            try
            {
                result = await named.Check.CheckHealthAsync(new HealthCheckContext(), timeout.Token);
            }
            catch (Exception ex)
            {
                result = HealthCheckResult.Unhealthy(
                    HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach(named.Component)));
            }

            var latencyMs = (int)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            results.Add(new HealthResult(named.Component, Map(result.Status), latencyMs, checkedAt, result.Description));
        }

        var transitions = await healthLog.RecordAsync(results, cancellationToken);
        await notifier.NotifyAsync(transitions, cancellationToken);
    }

    private static Platform.Modules.Operations.Contracts.HealthStatus Map(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus status) => status switch
    {
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy => Platform.Modules.Operations.Contracts.HealthStatus.Healthy,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded => Platform.Modules.Operations.Contracts.HealthStatus.Degraded,
        _ => Platform.Modules.Operations.Contracts.HealthStatus.Unhealthy,
    };
}
