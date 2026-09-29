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
/// <see cref="RevocationAuditRetry"/> runs every <see cref="RetryEvery"/>; the queue holds at most
/// <see cref="Capacity"/> entries and logs anything beyond it at error level with the user and tenant, so the log is the
/// record of last resort. The queue lives in memory: a process that stops before the retry loses what is queued, which
/// the error log line of the failed write then records. Singleton.
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
        if (!await TryWriteAsync(tenant, entry, cancellationToken))
        {
            Queue(tenant, entry);
        }
    }

    /// <summary>Tries every queued entry once; those that fail again stay queued.</summary>
    public async Task RetryPendingAsync(CancellationToken cancellationToken)
    {
        for (var remaining = _pending.Count; remaining > 0 && _pending.TryDequeue(out var item); remaining--)
        {
            if (!await TryWriteAsync(item.Tenant, item.Entry, cancellationToken))
            {
                _pending.Enqueue(item);
            }
        }
    }

    private async Task<bool> TryWriteAsync(TenantContext tenant, AuditEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(tenant);
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>().WriteAsync(entry, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            WriteFailed(logger, entry.Action, entry.SubjectId, tenant.Slug, ex.GetType().Name);
            return false;
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

/// <summary>Retries queued revocation audits every <see cref="RevocationAuditLog.RetryEvery"/> while the host runs.</summary>
internal sealed class RevocationAuditRetry(RevocationAuditLog log, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RevocationAuditLog.RetryEvery, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await log.RetryPendingAsync(stoppingToken);
        }
    }
}
