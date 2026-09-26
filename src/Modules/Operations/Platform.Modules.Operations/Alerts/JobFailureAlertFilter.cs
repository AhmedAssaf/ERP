using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Modules.Operations.Alerts;

/// <summary>
/// F-60 "a job fails three times in a row, one alert": an <see cref="IElectStateFilter"/>, not an
/// <see cref="IApplyStateFilter"/>, because Hangfire's built-in <c>AutomaticRetryAttribute</c> (default order 20)
/// rewrites a failed attempt's candidate state from <see cref="FailedState"/> to a retry state during election
/// itself; by the time states are *applied*, only the final, retries-exhausted failure would still read as Failed.
/// This filter must therefore run before that rewrite. Hangfire only honours a job filter's <see cref="Order"/>
/// when the instance also implements <see cref="IJobFilter"/> (<c>Hangfire.Common.JobFilter</c>'s constructor checks
/// for it explicitly); <see cref="IElectStateFilter"/> alone does not extend it, so this filter implements both.
/// <para>
/// The streak lives in <c>ops.job_failure_streaks</c>, keyed by the job's <c>RecurringJobId</c> parameter when it has
/// one (each run of a recurring job such as "health-check", which has no retries, is a new job id) and by the job id
/// otherwise. A Succeeded candidate deletes the row, so a success resets the streak; the third consecutive failure
/// claims the row's <c>alerted</c> flag and sends one alert, and no further alert goes out until the streak resets.
/// A send that fails releases the claim so the next failure tries again. N-10: the alert names the job as
/// <c>Type.Method</c> only, never its arguments.
/// </para>
/// Registered per job server (<see cref="Platform.Shared.Jobs.JobsModule"/>), never via Hangfire's static
/// <c>GlobalJobFilters</c>, so it cannot leak into another Hangfire server sharing the same test process.
/// </summary>
internal sealed partial class JobFailureAlertFilter(
    IServiceScopeFactory scopeFactory, TimeProvider clock, AlertSettings settings, ILogger<JobFailureAlertFilter> logger)
    : IElectStateFilter, IJobFilter
{
    internal const string RecurringJobIdParameter = "RecurringJobId";
    private const int AlertAtFailureCount = 3;

    // Lower than AutomaticRetryAttribute's default Order (20): this filter must see the untouched Failed candidate
    // before that filter rewrites it into a retry.
    public int Order => -100;

    // Only one instance of this filter is ever registered (a DI singleton); irrelevant either way.
    public bool AllowMultiple => false;

    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var isSuccess = context.CandidateState is SucceededState;
        if (!isSuccess && context.CandidateState is not FailedState)
        {
            return;
        }

        var recurringJobId = context.GetJobParameter<string>(RecurringJobIdParameter);
        var jobKey = recurringJobId is { Length: > 0 } ? $"recurring:{recurringJobId}" : $"job:{context.BackgroundJob.Id}";

        // Never let an alerting problem (database or SMTP down) break the job's own state transition: log and move on.
        try
        {
            using var scope = scopeFactory.CreateScope();
            using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContext();

            if (isSuccess)
            {
                db.Database.ExecuteSql($"delete from ops.job_failure_streaks where job_key = {jobKey}");
                return;
            }

            var now = clock.GetUtcNow();
            db.Database.ExecuteSql(
                $"""
                 insert into ops.job_failure_streaks as s (job_key, failures, alerted, updated_at)
                 values ({jobKey}, 1, false, {now})
                 on conflict (job_key) do update set failures = s.failures + 1, updated_at = excluded.updated_at
                 """);

            // Claiming the flag in one statement means two workers failing the same recurring job at once still
            // send a single alert.
            var claimed = db.Database.ExecuteSql(
                $"""
                 update ops.job_failure_streaks set alerted = true
                 where job_key = {jobKey} and failures >= {AlertAtFailureCount} and not alerted
                 """);
            if (claimed == 0)
            {
                return;
            }

            if (!TryAlert(scope.ServiceProvider, context.BackgroundJob.Id, DisplayName(context.BackgroundJob.Job), recurringJobId, now))
            {
                db.Database.ExecuteSql($"update ops.job_failure_streaks set alerted = false where job_key = {jobKey}");
            }
        }
        catch (Exception ex)
        {
            LogStreakFailed(logger, ex.GetType().Name);
        }
    }

    /// <summary>N-10: <c>Type.Method</c> only; <c>Job.ToString()</c> is not relied on and arguments are never read.</summary>
    internal static string DisplayName(Job? job) =>
        job is null ? "(unknown job)" : $"{job.Type.Name}.{job.Method.Name}";

    private bool TryAlert(IServiceProvider services, string jobId, string jobDisplayName, string? recurringJobId, DateTimeOffset at)
    {
        var sender = services.GetRequiredService<IAlertSender>();
        var message = AlertMessages.JobFailedThreeTimes(jobId, jobDisplayName, recurringJobId, at, settings.BoardUrl);

        try
        {
            // IElectStateFilter is synchronous (Hangfire OSS has no async filter pipeline); alerts are rare enough
            // that blocking the worker thread for one SMTP round-trip is acceptable.
            sender.SendAsync(message, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            LogAlertFailed(logger, jobDisplayName, ex.GetType().Name);
            return false;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The job failure streak could not be updated ({ErrorType}); this state change is not counted.")]
    private static partial void LogStreakFailed(ILogger logger, string errorType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The failure alert for job {Job} could not be sent ({ErrorType}); the next failure tries again.")]
    private static partial void LogAlertFailed(ILogger logger, string job, string errorType);
}
