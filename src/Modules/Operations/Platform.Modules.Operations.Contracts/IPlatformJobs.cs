namespace Platform.Modules.Operations.Contracts;

/// <summary>
/// A Hangfire job in the Failed state (F-54 as narrowed). Only the job's <c>Type.Method</c> name and the exception type
/// are exposed, never its arguments or the exception message (N-10).
/// </summary>
public sealed record FailedJob(string JobId, string Name, Guid? TenantId, DateTimeOffset? FailedAt, string? ExceptionType);

/// <summary>The outcome of a re-run request.</summary>
public enum RequeueOutcome
{
    Requeued,

    /// <summary>The job no longer exists or is no longer failed (someone re-ran it, or it expired); nothing was changed.</summary>
    NotFailed,
}

/// <summary>Failed background jobs and the audited re-run action of the platform console (spec 3.4, D-8).</summary>
public interface IPlatformJobs
{
    /// <summary>The most recent failed jobs, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<FailedJob>> FailedAsync(int limit = 500, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a failed job back to the queue and writes <c>ops.platform_audit</c> with the admin, the job and the tenant
    /// the job carries. Call only after the PlatformAdmin policy has passed.
    /// </summary>
    Task<RequeueOutcome> RequeueAsync(string jobId, string actorId, CancellationToken cancellationToken = default);
}
