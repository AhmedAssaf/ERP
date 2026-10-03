using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Storage;

namespace Platform.Shared.Jobs;

/// <summary>
/// The worker's single decision on a stored job row (W-36 allow-list, W-42 authenticity and replay). Two uses:
/// <list type="bullet">
/// <item><see cref="Refusal"/>, read-only: the allow-list, the signature and whether the nonce already ran as another job.
/// <see cref="JobAllowListFilter"/> applies it before the job is performed and again in the retry election, so a refused
/// row fails once and is not retried.</item>
/// <item><see cref="Admit"/>, by <see cref="TenantJobActivator"/> right before the job's class is resolved: it reads the
/// tenant and the signature once, checks the allow-list's tenant rule and the signature over exactly those values and the
/// job as loaded, binds the nonce to the job id, and returns the binding the job then runs with. A row whose parameters
/// change after the filter's read is therefore judged on the values actually used.</item>
/// </list>
/// </summary>
public sealed class JobGate
{
    private readonly JobAuthenticity _authenticity;
    private readonly JobReplayLedger _ledger;
    private readonly ConcurrentDictionary<string, JobReplayLedger.RunLock> _running = new(StringComparer.Ordinal);

    internal JobGate(JobAuthenticity authenticity, JobReplayLedger ledger)
    {
        _authenticity = authenticity;
        _ledger = ledger;
    }

    /// <summary>Why the job must not run, or null when it may (nothing is written).</summary>
    public string? Refusal(IStorageConnection connection, BackgroundJob backgroundJob)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(backgroundJob);
        var stored = JobBinding.Read(connection, backgroundJob.Id);
        if (JobAllowList.Refusal(backgroundJob.Job, stored.RawTenantId ?? TenantOf(stored)) is { } refused)
        {
            return refused;
        }

        if (stored.Refusal is { } malformed)
        {
            return malformed;
        }

        var token = _authenticity.Verify(stored.Token, backgroundJob.Job, stored.Binding, out var unsigned);
        if (token is not { } valid)
        {
            return unsigned;
        }

        return _ledger.IsRunning(valid) ? AlreadyRunning : _ledger.Check(valid, backgroundJob.Id);
    }

    /// <summary>
    /// The binding the job runs with; throws <see cref="JobRefusedException"/> when the row is refused. An admitted job holds
    /// its run lock until <see cref="Finish"/> (fix round 2), so a second run of the same job id meanwhile is refused.
    /// </summary>
    public JobBinding Admit(IStorageConnection connection, BackgroundJob backgroundJob)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(backgroundJob);
        var stored = JobBinding.Read(connection, backgroundJob.Id);
        var refusal = JobAllowList.Refusal(backgroundJob.Job, stored.RawTenantId ?? TenantOf(stored)) ?? stored.Refusal;
        JobReplayLedger.RunLock? runLock = null;
        if (refusal is null)
        {
            var token = _authenticity.Verify(stored.Token, backgroundJob.Job, stored.Binding, out refusal);
            if (token is { } valid)
            {
                runLock = _ledger.TryLockRun(valid);
                refusal = runLock is null ? AlreadyRunning : _ledger.Claim(valid, backgroundJob.Id);
            }
        }

        if (refusal is null && runLock is not null && !_running.TryAdd(backgroundJob.Id, runLock))
        {
            refusal = AlreadyRunning;
        }

        if (refusal is not null)
        {
            runLock?.Dispose();
            throw new JobRefusedException($"Job {backgroundJob.Id} is refused: {refusal}.");
        }

        return stored.Binding;
    }

    /// <summary>
    /// Ends an admitted run: when it <paramref name="succeeded"/>, its signature is marked completed and never admits a run
    /// again; then its run lock is released, after the completion, so no second run slips in between. Nothing happens for a
    /// job this gate did not admit.
    /// </summary>
    public void Finish(string jobId, bool succeeded)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        try
        {
            if (succeeded)
            {
                _ledger.Complete(jobId);
            }
        }
        finally
        {
            if (_running.TryRemove(jobId, out var runLock))
            {
                runLock.Dispose();
            }
        }
    }

    private const string AlreadyRunning = "the job is already running (another run of the same job holds its signature)";

    /// <summary>The <c>Tenant</c> snapshot's id when only the snapshot is present, so the allow-list's tenant rule sees it too.</summary>
    private static string? TenantOf(StoredBinding stored) => stored.Binding.Tenant?.TenantId.ToString("D");
}
