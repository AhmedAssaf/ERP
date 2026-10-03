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
        return token is { } valid ? _ledger.Check(valid, backgroundJob.Id) : unsigned;
    }

    /// <summary>The binding the job runs with; throws <see cref="JobRefusedException"/> when the row is refused.</summary>
    public JobBinding Admit(IStorageConnection connection, BackgroundJob backgroundJob)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(backgroundJob);
        var stored = JobBinding.Read(connection, backgroundJob.Id);
        var refusal = JobAllowList.Refusal(backgroundJob.Job, stored.RawTenantId ?? TenantOf(stored)) ?? stored.Refusal;
        if (refusal is null)
        {
            var token = _authenticity.Verify(stored.Token, backgroundJob.Job, stored.Binding, out refusal);
            if (token is { } valid)
            {
                refusal = _ledger.Claim(valid, backgroundJob.Id);
            }
        }

        if (refusal is not null)
        {
            throw new JobRefusedException($"Job {backgroundJob.Id} is refused: {refusal}.");
        }

        return stored.Binding;
    }

    /// <summary>The <c>Tenant</c> snapshot's id when only the snapshot is present, so the allow-list's tenant rule sees it too.</summary>
    private static string? TenantOf(StoredBinding stored) => stored.Binding.Tenant?.TenantId.ToString("D");
}
