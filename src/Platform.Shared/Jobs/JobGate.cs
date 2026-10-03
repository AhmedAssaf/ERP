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
/// Fix round 3: the run lock <see cref="Admit"/> takes belongs to the run that took it. <see cref="JobAllowListFilter"/> opens a
/// <see cref="RunSlot"/> per run (<see cref="BeginRun"/>) and keeps it in that run's perform context; Hangfire calls the
/// filter's OnPerforming, the activator and OnPerformed one after the other on the run's worker thread, so the activator
/// finds the run's slot through a thread-static reference and puts its lock there, and <see cref="Finish"/> releases only the
/// lock in the slot it is given. A run refused in <see cref="Admit"/> holds no lock and releases none.
/// </summary>
public sealed class JobGate
{
    private const string AlreadyRunning = "the job is already running (another run of the same job holds its signature)";

    [ThreadStatic]
    private static RunSlot? _current;

    private readonly JobAuthenticity _authenticity;
    private readonly JobReplayLedger _ledger;

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
    /// its run lock in its own <see cref="RunSlot"/> until <see cref="Finish"/> (fix rounds 2 and 3), so a second run of the
    /// same job id meanwhile is refused.
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

        // The run's own slot, opened by the filter on this thread for this job; without one (an activator used without the
        // job server's filter) the run is refused rather than left holding a lock nobody releases.
        var slot = _current;
        if (refusal is null && (slot is null || slot.Lock is not null || !string.Equals(slot.JobId, backgroundJob.Id, StringComparison.Ordinal)))
        {
            refusal = "the job was not opened by the worker's job filter";
        }

        if (refusal is not null)
        {
            runLock?.Dispose();
            throw new JobRefusedException($"Job {backgroundJob.Id} is refused: {refusal}.");
        }

        slot!.Lock = runLock;
        return stored.Binding;
    }

    /// <summary>Opens the slot of the run of <paramref name="jobId"/> starting on this thread; the filter keeps it in the run's context.</summary>
    public static RunSlot BeginRun(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var slot = new RunSlot(jobId);
        _current = slot;
        return slot;
    }

    /// <summary>
    /// Ends the run of <paramref name="slot"/>: when it was admitted and <paramref name="succeeded"/>, its signature is marked
    /// completed and never admits a run again; then the run's own lock is released, after the completion, so no second run
    /// slips in between. A run that was refused (no lock in its slot) completes and releases nothing.
    /// </summary>
    public void Finish(RunSlot slot, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (ReferenceEquals(_current, slot))
        {
            _current = null;
        }

        var runLock = slot.Lock;
        if (runLock is null)
        {
            return;
        }

        slot.Lock = null;
        try
        {
            if (succeeded)
            {
                _ledger.Complete(slot.JobId);
            }
        }
        finally
        {
            runLock.Dispose();
        }
    }

    /// <summary>One run of a job: the run lock its admission took, if any. Only the run's own filter call releases it.</summary>
    public sealed class RunSlot
    {
        internal RunSlot(string jobId) => JobId = jobId;

        internal string JobId { get; }

        internal JobReplayLedger.RunLock? Lock { get; set; }

        /// <summary>True while the run holds its run lock.</summary>
        public bool HoldsLock => Lock is not null;
    }

    /// <summary>The <c>Tenant</c> snapshot's id when only the snapshot is present, so the allow-list's tenant rule sees it too.</summary>
    private static string? TenantOf(StoredBinding stored) => stored.Binding.Tenant?.TenantId.ToString("D");
}
