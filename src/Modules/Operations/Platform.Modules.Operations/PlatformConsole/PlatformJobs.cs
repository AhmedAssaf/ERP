using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Platform.Modules.Operations.Alerts;
using Platform.Modules.Operations.Contracts;
using Platform.Shared.Jobs;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Operations.PlatformConsole;

/// <summary>
/// Failed Hangfire jobs for the console, matched to tenants by the <c>TenantId</c> job parameter the client stamps
/// (<see cref="TenantJobFilter"/>), and the audited re-run (spec 3.4, D-8). Hangfire's storage API is synchronous, so
/// its calls run on the thread pool and never block a Blazor circuit.
/// </summary>
internal sealed class PlatformJobs(JobStorage storage, IPlatformAudit audit, IActingUserAccessor actingUser) : IPlatformJobs
{
    public const string RequeuedAction = "job.requeued";

    public Task<IReadOnlyList<FailedJob>> FailedAsync(int limit = 500, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return Task.Run<IReadOnlyList<FailedJob>>(() => ReadFailed(limit), cancellationToken);
    }

    public async Task<RequeueOutcome> RequeueAsync(string jobId, string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        // W-41: the audit row can only name the acting user, so a mismatch fails before the job moves, not after.
        if (!string.Equals(actingUser.UserId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A job is re-run by the acting user of the console request or circuit.");
        }

        // The tenant and the job's name come from storage, never from the page, so the audit row states the facts.
        var failed = await Task.Run(() => ReadIfFailed(jobId), cancellationToken);
        if (failed is null)
        {
            return RequeueOutcome.NotFailed;
        }

        // Only a job still in Failed moves: a concurrent re-run or a job that already moved on is left alone.
        var changed = await Task.Run(
            () => new BackgroundJobClient(storage).ChangeState(jobId, new EnqueuedState(), FailedState.StateName), cancellationToken);
        if (!changed)
        {
            return RequeueOutcome.NotFailed;
        }

        // Written after the state change, so no audit row claims a re-run that did not happen. If this write fails, the
        // exception reaches the page, which reports the action as failed.
        await audit.WriteAsync(
            new PlatformAuditEntry(actorId, RequeuedAction, "job", jobId, new Dictionary<string, string?>
            {
                ["tenant_id"] = failed.TenantId?.ToString(),
                ["job"] = failed.Name,
            }),
            cancellationToken);
        return RequeueOutcome.Requeued;
    }

    private List<FailedJob> ReadFailed(int limit)
    {
        var jobs = storage.GetMonitoringApi().FailedJobs(0, limit);
        using var connection = storage.GetConnection();
        return
        [
            .. jobs.Select(pair => new FailedJob(
                pair.Key,
                NameOf(pair.Value.Job, pair.Value.InvocationData),
                TenantOf(connection, pair.Key),
                pair.Value.FailedAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null,
                pair.Value.ExceptionType)),
        ];
    }

    private FailedJob? ReadIfFailed(string jobId)
    {
        using var connection = storage.GetConnection();
        var data = connection.GetJobData(jobId);
        if (data is null || !string.Equals(data.State, FailedState.StateName, StringComparison.Ordinal))
        {
            return null;
        }

        return new FailedJob(jobId, NameOf(data.Job, data.InvocationData), TenantOf(connection, jobId), null, null);
    }

    private static Guid? TenantOf(IStorageConnection connection, string jobId) =>
        connection.GetJobParameter(jobId, TenantJobFilter.TenantIdParameter) is { Length: > 0 } raw
            ? SerializationHelper.Deserialize<Guid?>(raw)
            : null;

    /// <summary>
    /// N-10: <c>Type.Method</c> only. A job whose type this host cannot load still has its invocation data; its type
    /// name is cut to the simple name, without namespace or assembly.
    /// </summary>
    private static string NameOf(Job? job, InvocationData? invocation)
    {
        if (job is not null || invocation is null)
        {
            return JobFailureAlertFilter.DisplayName(job);
        }

        var typeName = invocation.Type.Split(',')[0];
        return $"{typeName[(typeName.LastIndexOf('.') + 1)..]}.{invocation.Method}";
    }
}
