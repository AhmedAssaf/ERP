using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Jobs;

/// <summary>
/// Told after every pass of <see cref="RecurringJobGuard"/>: the ids it restored and the unknown ids it removed (both empty
/// when every entry was intact). The Operations module records it as health component "Jobs", so a restore or a removal
/// opens an F-60 incident and the next intact pass closes it.
/// </summary>
public interface IRecurringJobDriftReporter
{
    Task ReportAsync(IReadOnlyList<string> restoredJobIds, IReadOnlyList<string> removedJobIds, DateTimeOffset checkedAt, CancellationToken cancellationToken);
}

/// <summary>
/// W-42: the worker checks its recurring jobs on a timer (<see cref="JobServerSettings.RecurringJobGuardInterval"/>, five
/// minutes by default) and writes back any entry that went missing or was altered (<see cref="RecurringJobCatalog.Drifted"/>),
/// so a deleted or re-timed health check, scan, cleanup or alert no longer stalls until the worker restarts; it removes an id in
/// the recurring set that it does not define when its job is one no worker may run (<see cref="RecurringJobCatalog.Unknown"/>);
/// an unknown entry naming a platform job, or a type this build does not know (a newer worker's, during a deploy), is left in
/// place and logged. Each restore and removal is logged (job ids only) and reported (<see cref="IRecurringJobDriftReporter"/>). The same pass prunes the replay ledger. A
/// failed pass is logged with its exception type and tried again on the next tick; it never stops the worker.
/// </summary>
internal sealed partial class RecurringJobGuard(
    RecurringJobCatalog catalog,
    JobReplayLedger ledger,
    IEnumerable<IRecurringJobDriftReporter> reporters,
    JobServerSettings settings,
    TimeProvider time,
    ILogger<RecurringJobGuard> logger) : BackgroundService
{
    /// <summary>One pass: check, restore, remove unknown ids, report, prune. Returns the restored and the removed ids.</summary>
    public async Task<IReadOnlyList<string>> RunOnceAsync(CancellationToken cancellationToken)
    {
        // Hangfire's storage API is synchronous; off the timer's thread.
        var (restored, removed) = await Task.Run(
            () =>
            {
                var drifted = catalog.Drifted();
                foreach (var definition in drifted)
                {
                    catalog.Restore(definition);
                    LogRestored(logger, definition.Id);
                }

                var removed = new List<string>();
                foreach (var (id, disallowed) in catalog.Unknown())
                {
                    if (disallowed)
                    {
                        catalog.Remove(id);
                        removed.Add(id);
                        LogRemoved(logger, id);
                    }
                    else
                    {
                        LogLeft(logger, id);
                    }
                }

                return (drifted.Select(d => d.Id).ToList(), removed);
            },
            cancellationToken);

        var checkedAt = time.GetUtcNow();
        foreach (var reporter in reporters)
        {
            try
            {
                await reporter.ReportAsync(restored, removed, checkedAt, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogReportFailed(logger, exception.GetType().Name);
            }
        }

        try
        {
            await Task.Run(ledger.Prune, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPruneFailed(logger, exception.GetType().Name);
        }

        return [.. restored, .. removed];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.RecurringJobGuardInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogPassFailed(logger, exception.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recurring job {RecurringJobId} was missing or altered and has been restored.")]
    private static partial void LogRestored(ILogger logger, string recurringJobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring job {RecurringJobId} is not one this worker defines; its job may be another version's, so it is left in place.")]
    private static partial void LogLeft(ILogger logger, string recurringJobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recurring job {RecurringJobId} names a job no worker may run and has been removed.")]
    private static partial void LogRemoved(ILogger logger, string recurringJobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The recurring job check failed ({ErrorType}); it runs again on the next tick.")]
    private static partial void LogPassFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The recurring job check could not be reported ({ErrorType}).")]
    private static partial void LogReportFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The job replay ledger could not be pruned ({ErrorType}); it is tried again on the next tick.")]
    private static partial void LogPruneFailed(ILogger logger, string errorType);
}
