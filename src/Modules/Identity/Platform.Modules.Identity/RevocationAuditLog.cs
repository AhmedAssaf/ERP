using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Modules.Audit.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

/// <summary>
/// W-21: writes the end of a session (<c>identity.session_revoked</c>) to the tenant's audit log from a scope of its own,
/// so it does not depend on the request or circuit whose check found the removal (that scope can be disposed before the
/// shared check completes). A write that fails is queued and written by <see cref="RetryPendingAsync"/>, which
/// <see cref="RevocationAuditRetry"/> runs every <see cref="RetryEvery"/> and once more when the host stops.
/// <para>
/// Logging: the first failure of an entry is an error naming the user and tenant (the record of last resort); its later
/// failed retries are debug lines, and each retry cycle that leaves entries queued logs one warning with the queue size,
/// so an outage costs one error per ended session plus one warning per cycle, not one error per entry per cycle. The
/// queue holds at most <see cref="Capacity"/> entries; beyond it an entry is dropped with a critical line. The queue lives
/// in memory: what the last flush at shutdown cannot write is counted in a critical line.
/// </para>
/// Singleton.
/// </summary>
internal sealed partial class RevocationAuditLog(IServiceScopeFactory scopes, ILogger<RevocationAuditLog> logger)
{
    internal const int Capacity = 10_000;
    internal static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<(TenantContext Tenant, AuditEntry Entry)> _pending = new();

    public int PendingCount => _pending.Count;

    /// <summary>Writes the entry for <paramref name="tenant"/>, or queues it when the write fails. Never throws for a failed write.</summary>
    public async Task WriteOrQueueAsync(TenantContext tenant, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(entry);
        if (await TryWriteAsync(tenant, entry, cancellationToken) is { } errorType)
        {
            WriteFailed(logger, entry.Action, entry.SubjectId, tenant.Slug, errorType);
            Queue(tenant, entry);
        }
    }

    /// <summary>
    /// Tries every queued entry once; those that fail again stay queued (a debug line each). A cycle that leaves entries
    /// queued logs one warning with the queue size. Stops early, leaving the rest queued, when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task RetryPendingAsync(CancellationToken cancellationToken)
    {
        var tried = 0;
        for (var remaining = _pending.Count; remaining > 0 && !cancellationToken.IsCancellationRequested && _pending.TryDequeue(out var item); remaining--)
        {
            tried++;
            if (await TryWriteAsync(item.Tenant, item.Entry, cancellationToken) is { } errorType)
            {
                RetryFailed(logger, item.Entry.Action, item.Entry.SubjectId, item.Tenant.Slug, errorType);
                _pending.Enqueue(item);
            }
        }

        if (tried > 0 && !_pending.IsEmpty)
        {
            StillQueued(logger, _pending.Count);
        }
    }

    // Null when written; otherwise the failure's type name (never its message, N-10).
    private async Task<string?> TryWriteAsync(TenantContext tenant, AuditEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(tenant);
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(entry, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            return ex.GetType().Name;
        }
    }

    private void Queue(TenantContext tenant, AuditEntry entry)
    {
        if (_pending.Count >= Capacity)
        {
            Dropped(logger, entry.Action, entry.SubjectId, tenant.Slug);
            return;
        }

        _pending.Enqueue((tenant, entry));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit {Action} of user {UserId} on tenant {Tenant} was not written ({ErrorType}); it is queued for retry.")]
    private static partial void WriteFailed(ILogger logger, string action, string? userId, string tenant, string errorType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Retry of audit {Action} of user {UserId} on tenant {Tenant} failed again ({ErrorType}); it stays queued.")]
    private static partial void RetryFailed(ILogger logger, string action, string? userId, string tenant, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} session revocation audits are still queued after a retry.")]
    private static partial void StillQueued(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Audit {Action} of user {UserId} on tenant {Tenant} was dropped: the retry queue is full.")]
    private static partial void Dropped(ILogger logger, string action, string? userId, string tenant);
}

/// <summary>
/// The audit writer <see cref="MembershipRevalidator"/> gets in the host (W-21): the tenant is the current scope's, read
/// when the entry is written, and the write goes through <see cref="RevocationAuditLog"/> (a scope of its own, queued on
/// failure). Scoped; it holds nothing that the end of its scope disposes.
/// </summary>
internal sealed class RevocationAuditWriter(ITenantAccessor tenants, RevocationAuditLog log) : IAuditWriter
{
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var tenant = tenants.Current ?? throw new InvalidOperationException("A session revocation is audited in the host tenant's log; this scope has no tenant.");
        return log.WriteOrQueueAsync(tenant, entry, cancellationToken);
    }
}

/// <summary>
/// Retries queued revocation audits every <see cref="RevocationAuditLog.RetryEvery"/> while the host runs, and once more
/// when it stops, within <see cref="FlushTimeout"/>; whatever is still queued then is counted in a critical log line,
/// since it is lost with the process.
/// </summary>
internal sealed partial class RevocationAuditRetry(RevocationAuditLog log, TimeProvider clock, ILogger<RevocationAuditRetry> logger) : BackgroundService
{
    internal static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (log.PendingCount == 0)
        {
            return;
        }

        using var flush = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        flush.CancelAfter(FlushTimeout);
        await log.RetryPendingAsync(flush.Token);
        if (log.PendingCount is > 0 and var lost)
        {
            Unwritten(logger, lost);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RevocationAuditLog.RetryEvery, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await log.RetryPendingAsync(stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Count} session revocation audits were not written before the host stopped and are lost; the error log lines of their first failure name them.")]
    private static partial void Unwritten(ILogger logger, int count);
}
