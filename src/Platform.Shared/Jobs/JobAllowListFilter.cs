using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Jobs;

/// <summary>
/// Applies <see cref="JobAllowList"/> on every job server (W-36 fix round 1). Before a job is performed, and so before its
/// class is activated, a refused job throws <see cref="JobRefusedException"/> and fails without its method being invoked.
/// In the state election after Hangfire's automatic retry (order 20), a refused or unloadable job that was about to be
/// scheduled or, with a zero delay, enqueued again for a retry is failed instead, so a forged row is not run again ten times; the failure stays visible on the
/// console's failed-jobs page and counts towards the job failure alert.
/// </summary>
internal sealed partial class JobAllowListFilter(ILogger<JobAllowListFilter> logger) : IServerFilter, IElectStateFilter, IJobFilter
{
    public bool AllowMultiple => false;

    // After AutomaticRetryAttribute (20), so the election sees its retry and can replace it.
    public int Order => 30;

    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (JobAllowList.Refusal(context.Connection, context.BackgroundJob) is { } reason)
        {
            LogRefused(logger, context.BackgroundJob.Id, reason);
            throw new JobRefusedException($"Job {context.BackgroundJob.Id} is refused: {reason}.");
        }
    }

    public void OnPerformed(PerformedContext context)
    {
    }

    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsRetry(context))
        {
            return;
        }

        if (JobAllowList.Refusal(context.Connection, context.BackgroundJob) is { } reason)
        {
            context.CandidateState = new FailedState(new JobRefusedException($"Job {context.BackgroundJob.Id} is refused: {reason}."))
            {
                Reason = "Refused by the worker's job allow-list; not retried.",
            };
        }
    }

    /// <summary>
    /// A retry election: AutomaticRetry turns the Failed candidate of a performed job into Scheduled, or into Enqueued when
    /// its delay is zero (reason "Retry attempt ..."). Any Scheduled or Enqueued candidate for a job leaving Processing is
    /// treated as one, so neither form re-runs a refused row.
    /// </summary>
    private static bool IsRetry(ElectStateContext context) =>
        context.CandidateState is ScheduledState
        || (context.CandidateState is EnqueuedState enqueued
            && (string.Equals(context.CurrentState, ProcessingState.StateName, StringComparison.Ordinal)
                || (enqueued.Reason?.StartsWith("Retry attempt", StringComparison.Ordinal) ?? false)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} is refused and not run: {Reason}.")]
    private static partial void LogRefused(ILogger logger, string jobId, string reason);
}
