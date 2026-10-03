using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Scanning;
using Platform.Shared.Storage;
using Platform.Shared.Tenancy;
using Platform.Shared.Jobs;

namespace Platform.Modules.Vendors.Documents;

/// <summary>
/// The recurring job "vendor-document-rescan" (V-10), every five minutes in the worker: scans again every document the
/// scanner could not decide on. It lists pending documents across companies through the security-definer function
/// <c>vendor.pending_scan_documents</c> (ids only; the least recently tried first, and none tried 12 times already),
/// then handles each one in its own scope under that company's vendor context, so the application role never reads past
/// row-level security. A document without a verdict (clamd's size-limit error, a timeout or error reply, or a scanner
/// exception) goes to the back of the queue, and the scanner is probed at once with <see cref="Canary"/>, a small
/// known-clean buffer: when the canary scans clean the fault is the file's and it is charged one attempt now (parked
/// and audited at 12); when the canary gets no verdict either, clamd is down and nothing is charged. A storage failure
/// while reading the quarantined file, or any other exception, is never the file's fault and is not charged. Such
/// outages in a row (<see cref="MaxConsecutiveOutages"/>) stop the run; a scan verdict or a charged file resets the
/// count, while a missing file or a document no longer pending leaves it as it is.
/// </summary>
[PlatformJob]
internal sealed partial class VendorDocumentRescanJob(
    IDbContextFactory<VendorsDbContext> contexts, IServiceScopeFactory scopes, ILogger<VendorDocumentRescanJob> logger)
{
    private const int BatchSize = 200;

    /// <summary>Outages in a row (canary without a verdict, storage or other failure) after which a run stops.</summary>
    internal const int MaxConsecutiveOutages = 3;

    /// <summary>What the scanner is probed with when a document gets no verdict: plain text, clean by construction.</summary>
    internal static ReadOnlyMemory<byte> Canary { get; } = "WaslaBid virus scanner canary: plain text, known clean."u8.ToArray();

    private enum Step
    {
        /// <summary>Scanned to a verdict, or a failure charged to the file after a clean canary.</summary>
        Healthy,

        /// <summary>A missing file or a document no longer pending: says nothing about the scanner.</summary>
        Neutral,

        /// <summary>Scanner, storage or database not usable: nothing charged.</summary>
        Outage,
    }

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

        var outages = 0;
        foreach (var document in pending)
        {
            switch (await RescanAsync(document, cancellationToken))
            {
                case Step.Healthy:
                    outages = 0;
                    break;
                case Step.Outage when ++outages >= MaxConsecutiveOutages:
                    LogOutagesInARow(logger, outages);
                    return;
            }
        }
    }

    /// <summary>One document in its own scope under its company's vendor context.</summary>
    private async Task<Step> RescanAsync(PendingDocument document, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<VendorAccessor>().Set(new VendorContext(document.CompanyId));
        var documents = scope.ServiceProvider.GetRequiredService<VendorDocuments>();
        RescanOutcome outcome;
        try
        {
            outcome = await documents.RescanAsync(document.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // After the scan (storing, the database, the audit): not the file's fault.
            LogRescanFailed(logger, document.Id, ex.GetType().Name);
            await TouchAsync(documents, document.Id);
            return Step.Outage;
        }

        switch (outcome)
        {
            case RescanOutcome.Clean or RescanOutcome.Infected:
                return Step.Healthy;
            case RescanOutcome.FileMissing or RescanOutcome.NotPending:
                return Step.Neutral;
            case RescanOutcome.ScannerFailed or RescanOutcome.ScannerUnavailable:
                if (!await CanaryIsCleanAsync(scope.ServiceProvider.GetRequiredService<IVirusScanner>(), cancellationToken))
                {
                    return Step.Outage;
                }

                await ChargeAsync(documents, document.Id);
                return Step.Healthy;
            default:
                return Step.Outage;
        }
    }

    /// <summary>True when the scanner gives the known-clean canary a clean verdict, so it works right now.</summary>
    private async Task<bool> CanaryIsCleanAsync(IVirusScanner scanner, CancellationToken cancellationToken)
    {
        try
        {
            using var canary = new MemoryStream(Canary.ToArray(), writable: false);
            return (await scanner.ScanAsync(canary, cancellationToken)).Verdict == ScanVerdict.Clean;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCanaryFailed(logger, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Charges the file one attempt; when that fails (the database went away), it is logged and not retried.</summary>
    private async Task ChargeAsync(VendorDocuments documents, Guid documentId)
    {
        try
        {
            await documents.ChargeAttemptAsync(documentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAttemptNotCounted(logger, documentId, ex.GetType().Name);
        }
    }

    /// <summary>Moves the document to the back of the queue; when that fails too, it is logged.</summary>
    private async Task TouchAsync(VendorDocuments documents, Guid documentId)
    {
        try
        {
            await documents.TouchAsync(documentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotRotated(logger, documentId, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} vendor documents in a row met an outage of the virus scanner, object storage or the database; the run stops and charges none of them.")]
    private static partial void LogOutagesInARow(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The virus scanner canary could not be scanned ({ErrorType}); treated as an outage.")]
    private static partial void LogCanaryFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Vendor document {DocumentId} could not be moved to the back of the retry queue ({ErrorType}).")]
    private static partial void LogNotRotated(ILogger logger, Guid documentId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The retry scan of vendor document {DocumentId} failed ({ErrorType}); it stays pending.")]
    private static partial void LogRescanFailed(ILogger logger, Guid documentId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The retry attempt of vendor document {DocumentId} could not be counted ({ErrorType}).")]
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
[PlatformJob]
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
