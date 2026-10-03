using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Jobs;

/// <summary>
/// Applies <see cref="JobAllowList"/> (W-36 fix round 1) and, since W-42, the job's signature and replay check
/// (<see cref="JobGate.Refusal"/>) on every job server. Before a job is performed, and so before its
/// class is activated, a refused job throws <see cref="JobRefusedException"/> and fails without its method being invoked.
/// In the state election after Hangfire's automatic retry (order 20), a refused or unloadable job that was about to be
/// scheduled or, with a zero delay, enqueued again for a retry is failed instead, so a forged row is not run again ten times; the failure stays visible on the
/// console's failed-jobs page and counts towards the job failure alert.
/// </summary>
internal sealed partial class JobAllowListFilter(JobGate gate, ILogger<JobAllowListFilter> logger) : IServerFilter, IElectStateFilter, IJobFilter
{
    public bool AllowMultiple => false;

    private static readonly string RunSlotItem = typeof(JobAllowListFilter).FullName + ".RunSlot";

    // After AutomaticRetryAttribute (20), so the election sees its retry and can replace it.
    public int Order => 30;

    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (gate.Refusal(context.Connection, context.BackgroundJob) is { } reason)
        {
            LogRefused(logger, context.BackgroundJob.Id, reason);
            throw new JobRefusedException($"Job {context.BackgroundJob.Id} is refused: {reason}.");
        }

        // Fix round 3: this run's own slot; the activator puts the run lock it takes there, and OnPerformed releases only it.
        context.Items[RunSlotItem] = JobGate.BeginRun(context.BackgroundJob.Id);
    }

    /// <summary>
    /// W-42: a job that ran without an exception is marked completed in the replay ledger, so moving it back to the queue
    /// (from Succeeded) never runs it again; a failed job stays open for its retries and the console's re-run. Then the run
    /// lock taken at admission is released (fix round 2). A ledger that cannot be written is logged and does not fail a job
    /// that already ran; the lock is released either way.
    /// </summary>
    public void OnPerformed(PerformedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Items.TryGetValue(RunSlotItem, out var item) || item is not JobGate.RunSlot slot)
        {
            return;
        }

        context.Items.Remove(RunSlotItem);
        var succeeded = context.Exception is null || context.ExceptionHandled;
        try
        {
            gate.Finish(slot, succeeded);
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            LogCompletionNotRecorded(logger, context.BackgroundJob.Id, exception.GetType().Name);
        }
    }

    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsRetry(context))
        {
            return;
        }

        if (gate.Refusal(context.Connection, context.BackgroundJob) is { } reason)
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} succeeded, but its completion could not be recorded in the replay ledger ({ErrorType}).")]
    private static partial void LogCompletionNotRecorded(ILogger logger, string jobId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} is refused and not run: {Reason}.")]
    private static partial void LogRefused(ILogger logger, string jobId, string reason);
}
