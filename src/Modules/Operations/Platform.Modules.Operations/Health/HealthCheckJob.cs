using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Platform.Modules.Operations.Alerts;
using Platform.Modules.Operations.Contracts;
using ResultStatus = Platform.Modules.Operations.Contracts.HealthStatus;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// The recurring job "health-check" (plan task 3): runs every registered check with its own five-second timeout and
/// records one result per component through <see cref="IHealthLog"/>. A check that throws unexpectedly (it is
/// expected to catch its own failures) does not take the rest of the run down with it. Plan task 4 (F-60): then
/// <see cref="IncidentNotifier"/> sends every pending incident email.
/// When the results cannot be recorded (the health store is PostgreSQL, so a database outage is exactly when this
/// happens) the job alerts from process memory instead (<see cref="FallbackAlertState"/>): one "is down" email per
/// unhealthy component and one "cannot record health results" email, each once per outage. When recording works
/// again, components the fallback announced get a recovery email if healthy, or have their open incident marked as
/// already announced if still down, and the normal pipeline takes over. A store outage never makes the job throw: it
/// is logged as a warning (exception type only, N-10) so Hangfire does not mark every minute as failed.
/// W-10 (plan task 4): the run is one span with a child per check, and every result is published as a metric
/// (<see cref="HealthTelemetry"/>) before it is recorded, so the metrics do not depend on the store.
/// </summary>
internal sealed partial class HealthCheckJob(
    IEnumerable<NamedHealthCheck> checks,
    IHealthLog healthLog,
    IncidentNotifier notifier,
    FallbackAlertState fallback,
    IAlertSender sender,
    AlertSettings alertSettings,
    HealthTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<HealthCheckJob> logger)
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
        List<HealthResult> results;
        using (HealthTelemetry.StartRun())
        {
            results = await RunChecksAsync(cancellationToken);
        }

        telemetry.Publish(results);

        try
        {
            // The returned transitions are not needed here: the notifier works from the durable notified_* flags.
            await healthLog.RecordAsync(results, cancellationToken);
        }
        catch (Exception ex) when (IsNotCancellation(ex, cancellationToken))
        {
            LogStoreUnavailable(logger, ex.GetType().Name);
            await AlertFromMemoryAsync(results, ex.GetType().Name, cancellationToken);
            return;
        }

        try
        {
            await HandOverFromFallbackAsync(results, cancellationToken);
            await notifier.NotifyPendingAsync(cancellationToken);
        }
        catch (Exception ex) when (IsNotCancellation(ex, cancellationToken))
        {
            // The results are recorded; unsent incident emails stay pending in the database for the next run.
            LogNotifyFailed(logger, ex.GetType().Name);
        }
    }

    private async Task<List<HealthResult>> RunChecksAsync(CancellationToken cancellationToken)
    {
        var results = new List<HealthResult>();

        foreach (var named in checks)
        {
            using var activity = HealthTelemetry.StartCheck(named.Component);
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

            var elapsed = timeProvider.GetElapsedTime(startedAt);
            var status = Map(result.Status);
            telemetry.RecordDuration(named.Component, elapsed);
            HealthTelemetry.EndCheck(activity, status);
            results.Add(new HealthResult(named.Component, status, (int)elapsed.TotalMilliseconds, checkedAt, result.Description));
        }

        return results;
    }

    private async Task AlertFromMemoryAsync(List<HealthResult> results, string storeErrorType, CancellationToken cancellationToken)
    {
        foreach (var result in results)
        {
            if (result.Status != ResultStatus.Unhealthy || fallback.WasAnnouncedDown(result.Component))
            {
                continue;
            }

            if (await TrySendAsync(
                    AlertMessages.ComponentDown(result.Component, result.CheckedAt, result.Message, alertSettings.BoardUrl),
                    cancellationToken))
            {
                fallback.MarkAnnouncedDown(result.Component);
            }
        }

        if (!fallback.StoreAlerted &&
            await TrySendAsync(
                AlertMessages.HealthStoreUnavailable(storeErrorType, timeProvider.GetUtcNow(), alertSettings.BoardUrl),
                cancellationToken))
        {
            fallback.MarkStoreAlerted();
        }
    }

    private async Task HandOverFromFallbackAsync(List<HealthResult> results, CancellationToken cancellationToken)
    {
        fallback.ClearStoreAlert();
        var announced = fallback.AnnouncedDown();
        if (announced.Count == 0)
        {
            return;
        }

        // Before any pending email goes out: an incident for a component the admin already heard about is not new.
        await notifier.MarkOpenAlreadyAnnouncedAsync(announced, cancellationToken);

        foreach (var component in announced)
        {
            var result = results.FirstOrDefault(r => r.Component == component);
            switch (result?.Status)
            {
                case ResultStatus.Healthy:
                    if (await TrySendAsync(
                            AlertMessages.ComponentRecovered(component, result.CheckedAt, message: null, alertSettings.BoardUrl),
                            cancellationToken))
                    {
                        fallback.Forget(component);
                    }

                    break;

                case ResultStatus.Degraded:
                    // Neither down nor recovered: keep it, so a later Healthy result still sends the recovery email
                    // (Degraded never opens or closes an incident, so the normal pipeline would never send one).
                    break;

                default:
                    // Unhealthy (its open incident is recorded and now marked announced) or no longer checked at all.
                    fallback.Forget(component);
                    break;
            }
        }
    }

    private async Task<bool> TrySendAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(message, cancellationToken);
            return true;
        }
        catch (Exception ex) when (IsNotCancellation(ex, cancellationToken))
        {
            // Not remembered as sent, so the next run tries again.
            LogFallbackSendFailed(logger, ex.GetType().Name);
            return false;
        }
    }

    private static bool IsNotCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    private static ResultStatus Map(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus status) => status switch
    {
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy => ResultStatus.Healthy,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded => ResultStatus.Degraded,
        _ => ResultStatus.Unhealthy,
    };

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Health results could not be recorded ({ErrorType}); alerting from worker memory until the store is back.")]
    private static partial void LogStoreUnavailable(ILogger logger, string errorType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Incident alerts could not be processed ({ErrorType}); pending emails are retried on the next run.")]
    private static partial void LogNotifyFailed(ILogger logger, string errorType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A health alert email could not be sent ({ErrorType}); it is retried on the next run.")]
    private static partial void LogFallbackSendFailed(ILogger logger, string errorType);
}
