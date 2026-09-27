using System.Buffers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Storage;
using Platform.Shared.Tenancy;
using Platform.Shared.Text;

namespace Platform.Modules.Vendors.Documents;

/// <summary>
/// Chunked uploads (V-9, ADR-0001). The state is a <c>vendor.uploads</c> row under the company policy, so an upload of
/// another company is simply not found; the chunks are staged in object storage under <c>staging/{upload id}/{index}</c>.
/// Every chunk but the last is exactly <see cref="VendorDocumentLimits.ChunkBytes"/> long and carries its SHA-256, so a
/// chunk cut short by a dropped connection is refused and sent again; a chunk sent again replaces the one before.
/// Two bounds per company, counted under its row lock when an upload starts: at most <see cref="MaxOpenUploads"/> open
/// uploads (no outcome yet, with a start or a chunk in the last hour), and at most <c>Vendors:MaxUploadsPerDay</c> starts
/// in any 24 hours whatever became of them. Completion holds a row lock on the upload (<c>FOR UPDATE NOWAIT</c>), so a
/// completion retried while the first is still scanning is told to wait; it streams the chunks into a temporary file,
/// scans and stores it, and writes the document row and the upload's outcome in that one transaction, so completing
/// again always answers the first outcome and never adds a second document. A file refused for its type records the
/// outcome <c>refused</c> and its chunks go; an expiry refusal leaves the upload open for a corrected date. An infected
/// finding is audited before the outcome commits (at least once; the entry carries the upload id, so a duplicate is
/// recognisable). The document takes the upload's id, so a completion that failed after storing the file and is asked
/// again stores it under the same key, and the cleanup job can find the file of one that never recorded an outcome. An
/// upload is usable for a day, by the database's clock; the worker's cleanup job then removes it with its chunks.
/// </summary>
internal sealed partial class VendorUploads(
    IDbContextFactory<VendorsDbContext> contexts,
    IVendorAccessor vendors,
    VendorDocuments documents,
    IObjectStorage storage,
    IOptions<VendorsOptions> options,
    ILogger<VendorUploads> logger) : IVendorUploads
{
    /// <summary>How long an upload may take from start to completion; the cleanup job removes it after that.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>Open uploads (no outcome yet, with a start or a chunk in the last hour) a company may have at once.</summary>
    public const int MaxOpenUploads = 10;

    internal const string Refused = "refused";

    private const int MaxFileNameLength = 255;
    private const string ChunkContentType = "application/octet-stream";
    private const string LockNotAvailable = "55P03";

    public async Task<Result<VendorUploadStarted>> StartAsync(VendorUploadStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        var companyId = RequireCompany();
        if (CheckStart(start) is { } refused)
        {
            return Result.Failure<VendorUploadStarted>(refused);
        }

        var chunkCount = (int)((start.Size + VendorDocumentLimits.ChunkBytes - 1) / VendorDocumentLimits.ChunkBytes);
        var row = new UploadRow
        {
            Id = Guid.CreateVersion7(),
            CompanyId = companyId,
            DocumentType = start.DocumentType!,
            FileName = start.FileName!.Trim(),
            ContentType = start.ContentType!,
            DeclaredSize = start.Size,
            ChunkSize = VendorDocumentLimits.ChunkBytes,
            ChunkCount = chunkCount,
        };
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Under the company's row lock, so two starts in parallel cannot both take the last place.
        await db.Database.ExecuteSqlAsync($"select 1 from vendor.companies where id = {companyId} for update", cancellationToken);
        var counts = await db.Database.SqlQuery<UploadCounts>($"""
            select count(*) filter (where outcome is null
                                    and greatest(created_at, coalesce(last_chunk_at, created_at)) > now() - interval '1 hour')::int as open_uploads,
                   count(*)::int as started
            from vendor.uploads
            where company_id = {companyId} and created_at > now() - interval '24 hours'
            """).SingleAsync(cancellationToken);
        if (counts.OpenUploads >= MaxOpenUploads || counts.Started >= options.Value.MaxUploadsPerDay)
        {
            return Result.Failure<VendorUploadStarted>(Error.Refused(
                VendorDocumentErrors.TooManyUploads, "Too many uploads are in progress; finish one or try again later."));
        }

        db.Uploads.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(new VendorUploadStarted(row.Id, row.ChunkSize, row.ChunkCount));
    }

    public async Task<Result<int>> PutChunkAsync(
        Guid uploadId, int index, ReadOnlyMemory<byte> content, string? sha256, CancellationToken cancellationToken = default)
    {
        RequireCompany();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var upload = await FindActiveAsync(db, uploadId, cancellationToken);
        if (upload is null)
        {
            return Result.Failure<int>(NotFound());
        }

        if (upload.Outcome is not null)
        {
            return Result.Failure<int>(Completed());
        }

        if (index < 0 || index >= upload.ChunkCount)
        {
            return Result.Failure<int>(Error.Validation(VendorDocumentErrors.ChunkOutOfRange, "The upload has no chunk with that number."));
        }

        var expected = index == upload.ChunkCount - 1
            ? upload.DeclaredSize - ((long)index * upload.ChunkSize)
            : upload.ChunkSize;
        if (content.Length != expected)
        {
            return Result.Failure<int>(Error.Validation(VendorDocumentErrors.ChunkWrongSize, "The chunk is not as long as expected; send it again."));
        }

        if (sha256 is null || !string.Equals(VendorDocumentFiles.Sha256Hex(content.Span), sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<int>(Error.Validation(VendorDocumentErrors.ChunkHashMismatch, "The chunk arrived damaged; send it again."));
        }

        await storage.PutAsync(VendorDocumentFiles.ChunkKey(uploadId, index), content, ChunkContentType, cancellationToken);

        // One statement, so chunks arriving in parallel never lose each other's index; the age is checked again here, by
        // the database's clock, since the upload may have expired while the chunk was stored.
        var received = await db.Database.SqlQuery<int>($"""
            update vendor.uploads
            set received_chunks = array(select distinct c from unnest(received_chunks || {index}) as c order by c),
                last_chunk_at = now()
            where id = {uploadId} and outcome is null and created_at > now() - interval '24 hours'
            returning cardinality(received_chunks) as "Value"
            """).ToListAsync(cancellationToken);
        return received is [var count] ? Result.Success(count) : Result.Failure<int>(Completed());
    }

    public async Task<Result<VendorDocumentAdded>> CompleteAsync(Guid uploadId, DateOnly expiresOn, CancellationToken cancellationToken = default)
    {
        var companyId = RequireCompany();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlAsync($"select 1 from vendor.uploads where id = {uploadId} for update nowait", cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == LockNotAvailable)
        {
            return Result.Failure<VendorDocumentAdded>(
                Error.Conflict(VendorDocumentErrors.UploadInProgress, "The upload is being completed; ask again in a moment."));
        }

        var upload = await FindActiveAsync(db, uploadId, cancellationToken);
        if (upload is null)
        {
            return Result.Failure<VendorDocumentAdded>(NotFound());
        }

        if (upload.Outcome is not null)
        {
            return PreviousOutcome(upload);
        }

        if (upload.ReceivedChunks.Length != upload.ChunkCount)
        {
            return Result.Failure<VendorDocumentAdded>(Incomplete());
        }

        await using var file = VendorDocumentFiles.CreateTempFile();
        var sha256 = await AssembleAsync(upload, file, cancellationToken);
        if (sha256 is null)
        {
            return Result.Failure<VendorDocumentAdded>(Incomplete());
        }

        var scanned = await documents.ScanAndStoreAsync(upload.Id, upload.DocumentType, expiresOn, file, sha256, cancellationToken);
        if (!scanned.IsSuccess)
        {
            if (scanned.Error.Code == VendorDocumentErrors.InvalidExpiry)
            {
                // Not the file's fault: the upload stays open with its chunks, for a completion with a corrected date.
                return Result.Failure<VendorDocumentAdded>(scanned.Error);
            }

            // The file itself is refused: recorded, so asking again answers the same and the upload no longer counts as open.
            upload.Outcome = Refused;
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            await DeleteChunksAsync(upload);
            return Result.Failure<VendorDocumentAdded>(scanned.Error);
        }

        // The scanner has answered: from here the outcome is recorded even when the caller goes away.
        Result<VendorDocumentAdded> outcome;
        if (scanned.Value.Document is { } document)
        {
            await VendorDocuments.RecordAsync(db, document);
            upload.Outcome = document.ScanStatus;
            upload.DocumentId = document.Id;
            upload.Sha256 = document.Sha256;
            outcome = Result.Success(VendorDocuments.Added(document));
        }
        else
        {
            upload.Outcome = "infected";
            outcome = VendorDocuments.InfectedResult();
        }

        await db.SaveChangesAsync(CancellationToken.None);
        if (scanned.Value.Signature is { } signature)
        {
            // Before the commit, so a finding is never recorded without its audit: when the audit fails the outcome rolls
            // back and completing again scans and audits again. A commit that fails after it leaves a second entry with the
            // same upload id, which a reader recognises as a duplicate (at least once, never lost).
            await documents.AuditUploadInfectedAsync(companyId, upload.Id, signature);
        }

        await transaction.CommitAsync(CancellationToken.None);
        await DeleteChunksAsync(upload);
        return outcome;
    }

    /// <summary>The refusal for a start request, or null when it may start.</summary>
    internal static Error? CheckStart(VendorUploadStart start)
    {
        if (VendorDocumentTypes.Find(start.DocumentType) is null)
        {
            return Error.Validation(VendorDocumentErrors.UnknownType, "Choose a commercial registration or VAT certificate.");
        }

        if (string.IsNullOrWhiteSpace(start.FileName) || start.FileName.Trim().Length > MaxFileNameLength
            || start.FileName.Any(char.IsControl) || TextSafety.HasInvisibleOrBidiControl(start.FileName))
        {
            return Error.Validation(VendorDocumentErrors.InvalidFileName, "The file name cannot be used.");
        }

        if (start.Size < 1)
        {
            return Error.Validation(VendorDocumentErrors.Empty, "The file is empty.");
        }

        if (start.Size > VendorDocumentLimits.MaxBytes)
        {
            return Error.Validation(VendorDocumentErrors.TooLarge, "The file is larger than 10 MB.");
        }

        return VendorDocumentLimits.ContentTypes.Contains(start.ContentType, StringComparer.Ordinal)
            ? null
            : Error.Validation(VendorDocumentErrors.WrongType, "The file is not a PDF, PNG or JPEG.");
    }

    /// <summary>The upload when it started less than a day ago by the database's clock (the same clock as the cleanup job).</summary>
    private static Task<UploadRow?> FindActiveAsync(VendorsDbContext db, Guid uploadId, CancellationToken cancellationToken) =>
        db.Uploads
            .FromSql($"select * from vendor.uploads where id = {uploadId} and created_at > now() - interval '24 hours'")
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Streams the chunks in order into <paramref name="file"/>, hashing as it goes, and rewinds it. Returns the file's
    /// SHA-256, or null when a chunk is missing or not the length it had when it arrived.
    /// </summary>
    private async Task<string?> AssembleAsync(UploadRow upload, Stream file, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            for (var index = 0; index < upload.ChunkCount; index++)
            {
                var length = Math.Min(upload.ChunkSize, upload.DeclaredSize - ((long)index * upload.ChunkSize));
                await using var chunk = await storage.OpenAsync(VendorDocumentFiles.ChunkKey(upload.Id, index), cancellationToken);
                if (chunk is null || chunk.Length != length)
                {
                    return null;
                }

                long copied = 0;
                int read;
                while ((read = await chunk.Content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    copied += read;
                    if (copied > length)
                    {
                        return null;
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (copied != length)
                {
                    return null;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await file.FlushAsync(cancellationToken);
        file.Position = 0;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Drops the staged chunks now; a failure is logged and left to the cleanup job, which removes them within a day.</summary>
    private async Task DeleteChunksAsync(UploadRow upload)
    {
        for (var index = 0; index < upload.ChunkCount; index++)
        {
            try
            {
                await storage.DeleteAsync(VendorDocumentFiles.ChunkKey(upload.Id, index), CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogChunkNotDeleted(logger, upload.Id, index, ex.GetType().Name);
            }
        }
    }

    private static Result<VendorDocumentAdded> PreviousOutcome(UploadRow upload) => upload.Outcome switch
    {
        "clean" => Result.Success(new VendorDocumentAdded(upload.DocumentId!.Value, upload.Sha256!, VendorDocumentStatus.Clean)),
        "pending_scan" => Result.Success(new VendorDocumentAdded(upload.DocumentId!.Value, upload.Sha256!, VendorDocumentStatus.PendingScan)),
        // Only the type check can refuse an assembled file: its size was checked at the start and chunk by chunk.
        Refused => Result.Failure<VendorDocumentAdded>(Error.Validation(VendorDocumentErrors.WrongType, "The file is not a PDF, PNG or JPEG.")),
        _ => VendorDocuments.InfectedResult(),
    };

    private Guid RequireCompany() =>
        vendors.Current?.CompanyId ?? throw new InvalidOperationException("Vendor uploads need the vendor context of a signed-in vendor.");

    private static Error NotFound() => Error.NotFound(VendorDocumentErrors.UploadNotFound, "The upload was not found; start it again.");

    private static Error Completed() => Error.Conflict(VendorDocumentErrors.UploadCompleted, "The upload is already complete.");

    private static Error Incomplete() => Error.Conflict(VendorDocumentErrors.UploadIncomplete, "Some chunks have not arrived yet; send them and complete again.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Staged chunk {Index} of upload {UploadId} could not be deleted ({ErrorType}); the cleanup job removes it.")]
    private static partial void LogChunkNotDeleted(ILogger logger, Guid uploadId, int index, string errorType);

    // Unmapped query types follow the context's snake_case naming convention: columns open_uploads and started.
    private sealed class UploadCounts
    {
        public int OpenUploads { get; set; }

        public int Started { get; set; }
    }
}
