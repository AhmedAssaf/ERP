using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Scanning;
using Platform.Shared.Storage;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Documents;

/// <summary>
/// The recurring job "vendor-document-rescan" (V-10), every five minutes in the worker: scans again every document the
/// scanner could not decide on. It lists pending documents across companies through the security-definer function
/// <c>vendor.pending_scan_documents</c> (ids only; the least recently tried first, and none tried 12 times already),
/// then handles each one in its own scope under that company's vendor context, so the application role never reads past
/// row-level security. A document without a verdict (clamd's size-limit error, a timeout or error reply, or an
/// exception from storage or the database) goes to the back of the queue and its attempt is deferred: it is charged
/// only once a later document in the same run is scanned to a verdict (clean or infected), which shows that storage,
/// the scanner and the database all work and the fault was the file's. A missing quarantine file (counted against that
/// document itself) or a document no longer pending proves nothing about the scanner: it neither charges nor clears the
/// deferred ones. After <see cref="MaxConsecutiveWithoutVerdict"/> documents without a verdict since the last one the
/// run stops, and the deferred ones are not charged: an outage of clamd, MinIO or the database counts nothing, however
/// long it lasts.
/// </summary>
internal sealed partial class VendorDocumentRescanJob(
    IDbContextFactory<VendorsDbContext> contexts, IServiceScopeFactory scopes, ILogger<VendorDocumentRescanJob> logger)
{
    private const int BatchSize = 200;

    /// <summary>Documents in a row without a verdict after which a run stops, charging none of them (the outage breaker).</summary>
    internal const int MaxConsecutiveWithoutVerdict = 3;

    // As the health-check job: no overlapping runs, and a failed run is not retried; the next one replaces it.
    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<PendingDocument> pending;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            pending = await db.Database.SqlQuery<PendingDocument>(
                $"select id, company_id from vendor.pending_scan_documents({BatchSize})")
                .ToListAsync(cancellationToken);
        }

        // Documents without a verdict since the last verdict, in order: charged when the next verdict comes.
        var deferred = new List<PendingDocument>();
        foreach (var document in pending)
        {
            var result = await RescanAsync(document, cancellationToken);
            if (result == Result.Neutral)
            {
                continue;
            }

            if (result == Result.Verdict)
            {
                foreach (var failed in deferred)
                {
                    await ChargeAsync(failed);
                }

                deferred.Clear();
                continue;
            }

            deferred.Add(document);
            if (deferred.Count >= MaxConsecutiveWithoutVerdict)
            {
                LogNoVerdictInARow(logger, deferred.Count);
                return;
            }
        }
    }

    private enum Result
    {
        /// <summary>Scanned to clean or infected: storage, scanner and database work.</summary>
        Verdict,

        /// <summary>A missing file or a document no longer pending: says nothing about the scanner.</summary>
        Neutral,

        /// <summary>No verdict: the file's fault or an outage, decided later in the run.</summary>
        None,
    }

    /// <summary>One document in its own scope under its company's vendor context.</summary>
    private async Task<Result> RescanAsync(PendingDocument document, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<VendorAccessor>().Set(new VendorContext(document.CompanyId));
        var documents = scope.ServiceProvider.GetRequiredService<VendorDocuments>();
        try
        {
            return await documents.RescanAsync(document.Id, cancellationToken) switch
            {
                RescanOutcome.Clean or RescanOutcome.Infected => Result.Verdict,
                RescanOutcome.FileMissing or RescanOutcome.NotPending => Result.Neutral,
                _ => Result.None,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRescanFailed(logger, document.Id, ex.GetType().Name);
            try
            {
                await documents.TouchAsync(document.Id);
            }
            catch (Exception touchFailure) when (touchFailure is not OperationCanceledException)
            {
                LogNotRotated(logger, document.Id, touchFailure.GetType().Name);
            }

            return Result.None;
        }
    }

    /// <summary>Charges a deferred attempt; when that fails (the database went away), it is logged and not retried.</summary>
    private async Task ChargeAsync(PendingDocument document)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<VendorAccessor>().Set(new VendorContext(document.CompanyId));
        try
        {
            await scope.ServiceProvider.GetRequiredService<VendorDocuments>().ChargeAttemptAsync(document.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAttemptNotCounted(logger, document.Id, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} vendor documents in a row got no scan verdict; the virus scanner or object storage is likely down, so the run stops and counts none of them.")]
    private static partial void LogNoVerdictInARow(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Vendor document {DocumentId} could not be moved to the back of the retry queue ({ErrorType}).")]
    private static partial void LogNotRotated(ILogger logger, Guid documentId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The retry scan of vendor document {DocumentId} failed ({ErrorType}); it stays pending.")]
    private static partial void LogRescanFailed(ILogger logger, Guid documentId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The deferred retry attempt of vendor document {DocumentId} could not be counted ({ErrorType}).")]
    private static partial void LogAttemptNotCounted(ILogger logger, Guid documentId, string errorType);

    // Unmapped query types follow the context's snake_case naming convention: columns id and company_id.
    private sealed class PendingDocument
    {
        public Guid Id { get; set; }

        public Guid CompanyId { get; set; }
    }
}

/// <summary>
/// The recurring job "vendor-upload-cleanup" (V-9), hourly in the worker: uploads started more than a day ago are
/// removed with their staged chunks, completed or not, once 25 hours old (an hour past their usable day, so a completion
/// that began at the edge has finished). The rows are found through the security-definer function
/// <c>vendor.stale_uploads</c>; each is then claimed in its own transaction with <c>vendor.claim_stale_upload</c>
/// (a row lock, <c>SKIP LOCKED</c>: a completion that still holds the row is skipped until the next run), its objects are
/// deleted, and the row is removed with <c>vendor.remove_stale_upload</c> before the lock is released. All three fix the
/// age themselves; the objects go first, so a row is never removed while its objects remain. An upload that never recorded an outcome may
/// still have left a file under its own id (a completion that stored it and then failed before its commit): both its
/// possible document and quarantine keys are deleted with it. No document row can name those keys, since a document
/// and its upload's outcome commit together.
/// </summary>
internal sealed partial class VendorUploadCleanupJob(
    IDbContextFactory<VendorsDbContext> contexts, IObjectStorage storage, ILogger<VendorUploadCleanupJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var stale = await db.Database.SqlQuery<StaleUpload>(
            $"select id, chunk_count, company_id, outcome from vendor.stale_uploads()").ToListAsync(cancellationToken);

        foreach (var candidate in stale)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var claimed = await db.Database.SqlQuery<StaleUpload>(
                    $"select id, chunk_count, company_id, outcome from vendor.claim_stale_upload({candidate.Id})").ToListAsync(cancellationToken);
                if (claimed is not [var upload])
                {
                    // A completion holds it (or it is already gone): the next run tries again.
                    await transaction.RollbackAsync(cancellationToken);
                    continue;
                }

                if (upload.Outcome is null)
                {
                    await storage.DeleteAsync(VendorDocumentFiles.DocumentKey(upload.CompanyId, upload.Id), cancellationToken);
                    await storage.DeleteAsync(VendorDocumentFiles.QuarantineKey(upload.CompanyId, upload.Id), cancellationToken);
                }

                for (var index = 0; index < upload.ChunkCount; index++)
                {
                    await storage.DeleteAsync(VendorDocumentFiles.ChunkKey(upload.Id, index), cancellationToken);
                }

                await db.Database.SqlQuery<bool>($"select vendor.remove_stale_upload({upload.Id}) as \"Value\"").ToListAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogCleanupFailed(logger, candidate.Id, ex.GetType().Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abandoned upload {UploadId} could not be removed ({ErrorType}); the next run tries again.")]
    private static partial void LogCleanupFailed(ILogger logger, Guid uploadId, string errorType);

    private sealed class StaleUpload
    {
        public Guid Id { get; set; }

        public int ChunkCount { get; set; }

        public Guid CompanyId { get; set; }

        public string? Outcome { get; set; }
    }
}
