using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// Completion holds a row lock on the upload (<c>FOR UPDATE NOWAIT</c>), so a completion retried while the first is still
/// scanning is told to wait instead of storing the document twice. An upload is usable for a day; the worker's cleanup
/// job then removes it with its chunks.
/// </summary>
internal sealed partial class VendorUploads(
    IDbContextFactory<VendorsDbContext> contexts,
    IVendorAccessor vendors,
    IVendorDocuments documents,
    IObjectStorage storage,
    TimeProvider clock,
    ILogger<VendorUploads> logger) : IVendorUploads
{
    /// <summary>How long an upload may take from start to completion; the cleanup job removes it after that.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

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
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            DocumentType = start.DocumentType!,
            FileName = start.FileName!.Trim(),
            ContentType = start.ContentType!,
            DeclaredSize = start.Size,
            ChunkSize = VendorDocumentLimits.ChunkBytes,
            ChunkCount = chunkCount,
        };
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.Uploads.Add(row);
        await db.SaveChangesAsync(cancellationToken);
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

        // One statement, so chunks arriving in parallel never lose each other's index.
        var received = await db.Database.SqlQuery<int>($"""
            update vendor.uploads
            set received_chunks = array(select distinct c from unnest(received_chunks || {index}) as c order by c)
            where id = {uploadId} and outcome is null
            returning cardinality(received_chunks) as "Value"
            """).ToListAsync(cancellationToken);
        return received is [var count] ? Result.Success(count) : Result.Failure<int>(Completed());
    }

    public async Task<Result<VendorDocumentAdded>> CompleteAsync(Guid uploadId, DateOnly expiresOn, CancellationToken cancellationToken = default)
    {
        RequireCompany();
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

        var file = await AssembleAsync(upload, cancellationToken);
        if (file is null)
        {
            return Result.Failure<VendorDocumentAdded>(Incomplete());
        }

        var added = await documents.AddAsync(upload.DocumentType, expiresOn, file, cancellationToken);
        if (added.IsSuccess)
        {
            upload.Outcome = added.Value.Status == VendorDocumentStatus.Clean ? "clean" : "pending_scan";
            upload.DocumentId = added.Value.DocumentId;
            upload.Sha256 = added.Value.Sha256;
        }
        else if (added.Error.Code == VendorDocumentErrors.Infected)
        {
            upload.Outcome = "infected";
        }
        else
        {
            // The file itself is refused (type, expiry); the chunks stay until the cleanup job, and asking again answers the same.
            return added;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await DeleteChunksAsync(upload, cancellationToken);
        return added;
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

    private Task<UploadRow?> FindActiveAsync(VendorsDbContext db, Guid uploadId, CancellationToken cancellationToken)
    {
        var startedAfter = clock.GetUtcNow() - Lifetime;
        return db.Uploads.SingleOrDefaultAsync(u => u.Id == uploadId && u.CreatedAt > startedAfter, cancellationToken);
    }

    /// <summary>The chunks in order as one file, or null when one is missing or not the length it had when it arrived.</summary>
    private async Task<byte[]?> AssembleAsync(UploadRow upload, CancellationToken cancellationToken)
    {
        var file = new byte[upload.DeclaredSize];
        for (var index = 0; index < upload.ChunkCount; index++)
        {
            var offset = (long)index * upload.ChunkSize;
            var length = (int)Math.Min(upload.ChunkSize, upload.DeclaredSize - offset);
            await using var chunk = await storage.OpenAsync(VendorDocumentFiles.ChunkKey(upload.Id, index), cancellationToken);
            if (chunk is null || chunk.Length != length)
            {
                return null;
            }

            await chunk.Content.ReadExactlyAsync(file.AsMemory((int)offset, length), cancellationToken);
        }

        return file;
    }

    /// <summary>Drops the staged chunks now; a failure is logged and left to the cleanup job, which removes them within a day.</summary>
    private async Task DeleteChunksAsync(UploadRow upload, CancellationToken cancellationToken)
    {
        for (var index = 0; index < upload.ChunkCount; index++)
        {
            try
            {
                await storage.DeleteAsync(VendorDocumentFiles.ChunkKey(upload.Id, index), cancellationToken);
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
        _ => Result.Failure<VendorDocumentAdded>(Error.Refused(VendorDocumentErrors.Infected, "The file contains a virus and was deleted.")),
    };

    private Guid RequireCompany() =>
        vendors.Current?.CompanyId ?? throw new InvalidOperationException("Vendor uploads need the vendor context of a signed-in vendor.");

    private static Error NotFound() => Error.NotFound(VendorDocumentErrors.UploadNotFound, "The upload was not found; start it again.");

    private static Error Completed() => Error.Conflict(VendorDocumentErrors.UploadCompleted, "The upload is already complete.");

    private static Error Incomplete() => Error.Conflict(VendorDocumentErrors.UploadIncomplete, "Some chunks have not arrived yet; send them and complete again.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Staged chunk {Index} of upload {UploadId} could not be deleted ({ErrorType}); the cleanup job removes it.")]
    private static partial void LogChunkNotDeleted(ILogger logger, Guid uploadId, int index, string errorType);
}
