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
/// row-level security. It stops when clamd cannot be reached or does not answer in time
/// (<see cref="ScanVerdict.Unavailable"/>), since every other document would wait the same; a document clamd answers
/// with an error for (<see cref="ScanVerdict.Failed"/>), or whose quarantined file is gone, counts an attempt and the run
/// goes on with the next. Any other failure on one document is logged and does not stop the others.
/// </summary>
internal sealed partial class VendorDocumentRescanJob(
    IDbContextFactory<VendorsDbContext> contexts, IServiceScopeFactory scopes, ILogger<VendorDocumentRescanJob> logger)
{
    private const int BatchSize = 200;

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

        foreach (var document in pending)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<VendorAccessor>().Set(new VendorContext(document.CompanyId));
            ScanVerdict? verdict;
            try
            {
                verdict = await scope.ServiceProvider.GetRequiredService<VendorDocuments>().RescanAsync(document.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogRescanFailed(logger, document.Id, ex.GetType().Name);
                continue;
            }

            if (verdict == ScanVerdict.Unavailable)
            {
                LogScannerUnavailable(logger, pending.Count);
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The virus scanner is still unavailable; {Count} vendor documents stay pending until the next run.")]
    private static partial void LogScannerUnavailable(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The retry scan of vendor document {DocumentId} failed ({ErrorType}); it stays pending.")]
    private static partial void LogRescanFailed(ILogger logger, Guid documentId, string errorType);

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
/// chunks go first, so a row is never removed while its chunks remain.
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
            $"select id, chunk_count from vendor.stale_uploads()").ToListAsync(cancellationToken);

        foreach (var upload in stale)
        {
            try
            {
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
    }
}
