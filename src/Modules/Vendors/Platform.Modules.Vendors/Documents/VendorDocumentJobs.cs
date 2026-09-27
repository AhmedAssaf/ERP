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
/// row-level security. It stops when clamd cannot be reached, does not answer in time or answers with an error about
/// itself (<see cref="ScanVerdict.Unavailable"/>), since every other document would wait the same. A document clamd
/// refuses for its size (<see cref="ScanVerdict.Failed"/>), or whose quarantined file is gone, counts an attempt and the
/// run goes on with the next; but after <see cref="MaxConsecutiveScannerErrors"/> such scanner errors in a row the run
/// stops, since clamd is more likely misconfigured than every file wrong, and the rest are not counted. Any other failure
/// on one document is logged and counts an attempt too, so it cannot keep a document retried forever.
/// </summary>
internal sealed partial class VendorDocumentRescanJob(
    IDbContextFactory<VendorsDbContext> contexts, IServiceScopeFactory scopes, ILogger<VendorDocumentRescanJob> logger)
{
    private const int BatchSize = 200;

    /// <summary>Scanner errors in a row after which a run stops without trying (and counting) the remaining documents.</summary>
    internal const int MaxConsecutiveScannerErrors = 3;

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

        var scannerErrors = 0;
        foreach (var document in pending)
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
                LogRescanFailed(logger, document.Id, ex.GetType().Name);
                await CountAttemptAsync(documents, document.Id);
                scannerErrors = 0;
                continue;
            }

            switch (outcome)
            {
                case RescanOutcome.ScannerUnavailable:
                    LogScannerUnavailable(logger, pending.Count);
                    return;
                case RescanOutcome.ScannerFailed when ++scannerErrors >= MaxConsecutiveScannerErrors:
                    LogScannerErrorsInARow(logger, scannerErrors);
                    return;
                case RescanOutcome.ScannerFailed:
                    break;
                default:
                    scannerErrors = 0;
                    break;
            }
        }
    }

    /// <summary>Counts the attempt of a retry scan that threw; when even that fails (the database is down), it is logged.</summary>
    private async Task CountAttemptAsync(VendorDocuments documents, Guid documentId)
    {
        try
        {
            await documents.CountAttemptAsync(documentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAttemptNotCounted(logger, documentId, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The virus scanner is still unavailable; {Count} vendor documents stay pending until the next run.")]
    private static partial void LogScannerUnavailable(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The retry scan of vendor document {DocumentId} failed ({ErrorType}); it stays pending.")]
    private static partial void LogRescanFailed(ILogger logger, Guid documentId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The failed retry scan of vendor document {DocumentId} could not be counted ({ErrorType}).")]
    private static partial void LogAttemptNotCounted(ILogger logger, Guid documentId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The virus scanner answered with an error for {Count} vendor documents in a row; the run stops and the rest wait for the next one. Check clamd's StreamMaxLength against ClamAv:MaxStreamBytes.")]
    private static partial void LogScannerErrorsInARow(ILogger logger, int count);

    // Unmapped query types follow the context's snake_case naming convention: columns id and company_id.
    private sealed class PendingDocument
    {
        public Guid Id { get; set; }

        public Guid CompanyId { get; set; }
    }
}

/// <summary>
/// The recurring job "vendor-upload-cleanup" (V-9), hourly in the worker: uploads started more than a day ago are
/// removed with their staged chunks, completed or not. The rows are found and removed through the security-definer
/// functions <c>vendor.stale_uploads</c> and <c>vendor.remove_stale_upload</c>, which fix that age themselves; the
/// objects go first, so a row is never removed while its objects remain. An upload that never recorded an outcome may
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

        foreach (var upload in stale)
        {
            try
            {
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogCleanupFailed(logger, upload.Id, ex.GetType().Name);
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
