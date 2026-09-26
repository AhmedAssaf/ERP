using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Scanning;
using Platform.Shared.Storage;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Documents;

/// <summary>
/// The documents of the current vendor context's company (F-12, V-8, V-10), and the retry scan of one pending document
/// for the worker. Every database call runs under the company's row-level security; the company is always the vendor
/// context's. A finding of the scanner is audited in the platform audit (<c>vendor.upload_infected</c>, subject the
/// company, data the signature name only): the file belongs to the platform-level company, not to the tenant whose host
/// it came through, and the retry scan has no tenant at all.
/// </summary>
internal sealed partial class VendorDocuments(
    IDbContextFactory<VendorsDbContext> contexts,
    IVendorAccessor vendors,
    IActingUserAccessor actingUser,
    IObjectStorage storage,
    IVirusScanner scanner,
    IPlatformAudit platformAudit,
    ILogger<VendorDocuments> logger) : IVendorDocuments
{
    public const string InfectedAction = "vendor.upload_infected";

    private const string Clean = "clean";
    private const string PendingScan = "pending_scan";
    private const string Infected = "infected";

    private static readonly DateOnly EarliestExpiry = new(2000, 1, 1);
    private static readonly DateOnly LatestExpiry = new(2100, 12, 31);

    public async Task<IReadOnlyList<VendorDocument>> ListAsync(CancellationToken cancellationToken = default)
    {
        var companyId = RequireCompany();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ScanStatus == Clean)
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Select(d => new VendorDocument(d.Id, d.Type, d.ExpiresOn, d.Sha256, d.IsCurrent, d.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<Result<VendorDocumentAdded>> AddAsync(
        string type, DateOnly expiresOn, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var companyId = RequireCompany();
        if (Check(type, expiresOn, content) is { } refused)
        {
            return Result.Failure<VendorDocumentAdded>(refused);
        }

        var contentType = VendorDocumentFiles.DetectContentType(content.Span)!;
        var sha256 = VendorDocumentFiles.Sha256Hex(content.Span);
        var documentId = Guid.CreateVersion7();

        var scan = await scanner.ScanAsync(AsStream(content), cancellationToken);
        switch (scan.Verdict)
        {
            case ScanVerdict.Infected:
                // Never stored, not even in quarantine; the caller drops the staged chunks.
                await AuditInfectedAsync(companyId, actingUser.UserId, scan.Signature!, cancellationToken);
                return Result.Failure<VendorDocumentAdded>(
                    Error.Refused(VendorDocumentErrors.Infected, "The file contains a virus and was deleted."));

            case ScanVerdict.Clean:
                {
                    var key = VendorDocumentFiles.DocumentKey(companyId, documentId);
                    await storage.PutAsync(key, content, contentType, cancellationToken);
                    await RecordAsync(new DocumentRow
                    {
                        Id = documentId,
                        CompanyId = companyId,
                        Type = type,
                        ExpiresOn = expiresOn,
                        ObjectKey = key,
                        Sha256 = sha256,
                        ScanStatus = Clean,
                        IsCurrent = true,
                    }, cancellationToken);
                    return Result.Success(new VendorDocumentAdded(documentId, sha256, VendorDocumentStatus.Clean));
                }

            default:
                {
                    var key = VendorDocumentFiles.QuarantineKey(companyId, documentId);
                    await storage.PutAsync(key, content, contentType, cancellationToken);
                    await RecordAsync(new DocumentRow
                    {
                        Id = documentId,
                        CompanyId = companyId,
                        Type = type,
                        ExpiresOn = expiresOn,
                        ObjectKey = key,
                        Sha256 = sha256,
                        ScanStatus = PendingScan,
                        IsCurrent = false,
                    }, cancellationToken);
                    LogPending(logger, companyId, documentId);
                    return Result.Success(new VendorDocumentAdded(documentId, sha256, VendorDocumentStatus.PendingScan));
                }
        }
    }

    /// <summary>
    /// The worker's retry scan of one pending document of the vendor context's company. Clean: moved out of quarantine
    /// and made current unless a newer file of its type already is. Infected: the file is deleted, the row kept as
    /// <c>infected</c> (never listed) and the finding audited. Returns the verdict, or null when the document is no longer
    /// pending or its quarantined file is gone (logged; it stays pending for a person to look at).
    /// </summary>
    public async Task<ScanVerdict?> RescanAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var companyId = RequireCompany();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Documents.SingleOrDefaultAsync(d => d.Id == documentId && d.ScanStatus == PendingScan, cancellationToken);
        if (row is null)
        {
            return null;
        }

        byte[] content;
        string contentType;
        await using (var stored = await storage.OpenAsync(row.ObjectKey, cancellationToken))
        {
            if (stored is null)
            {
                LogQuarantineMissing(logger, companyId, documentId);
                return null;
            }

            using var buffer = new MemoryStream();
            await stored.Content.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
            contentType = stored.ContentType;
        }

        var scan = await scanner.ScanAsync(new MemoryStream(content, writable: false), cancellationToken);
        switch (scan.Verdict)
        {
            case ScanVerdict.Clean:
                {
                    var quarantineKey = row.ObjectKey;
                    var key = VendorDocumentFiles.DocumentKey(companyId, documentId);
                    await storage.PutAsync(key, content, contentType, cancellationToken);
                    await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
                    {
                        await LockCompanyAsync(db, companyId, cancellationToken);
                        // A file uploaded later may have become current while this one waited; it stays current.
                        var newerCurrent = await db.Documents.AnyAsync(
                            d => d.Type == row.Type && d.IsCurrent && d.CreatedAt > row.CreatedAt, cancellationToken);
                        if (!newerCurrent)
                        {
                            await ReleaseCurrentAsync(db, companyId, row.Type, cancellationToken);
                        }

                        row.ScanStatus = Clean;
                        row.ObjectKey = key;
                        row.IsCurrent = !newerCurrent;
                        await db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                    }

                    await storage.DeleteAsync(quarantineKey, cancellationToken);
                    return ScanVerdict.Clean;
                }

            case ScanVerdict.Infected:
                await storage.DeleteAsync(row.ObjectKey, cancellationToken);
                row.ScanStatus = Infected;
                await db.SaveChangesAsync(cancellationToken);
                await AuditInfectedAsync(companyId, actorId: null, scan.Signature!, cancellationToken);
                return ScanVerdict.Infected;

            default:
                return ScanVerdict.Unavailable;
        }
    }

    /// <summary>The refusal for content that is not a document of the given type, or null when it may be scanned.</summary>
    internal static Error? Check(string type, DateOnly expiresOn, ReadOnlyMemory<byte> content)
    {
        if (VendorDocumentTypes.Find(type) is null)
        {
            return Error.Validation(VendorDocumentErrors.UnknownType, "Choose a commercial registration or VAT certificate.");
        }

        if (content.IsEmpty)
        {
            return Error.Validation(VendorDocumentErrors.Empty, "The file is empty.");
        }

        if (content.Length > VendorDocumentLimits.MaxBytes)
        {
            return Error.Validation(VendorDocumentErrors.TooLarge, "The file is larger than 10 MB.");
        }

        if (VendorDocumentFiles.DetectContentType(content.Span) is null)
        {
            return Error.Validation(VendorDocumentErrors.WrongType, "The file is not a PDF, PNG or JPEG.");
        }

        return expiresOn < EarliestExpiry || expiresOn > LatestExpiry
            ? Error.Validation(VendorDocumentErrors.InvalidExpiry, "Enter the document's expiry date.")
            : null;
    }

    private async Task RecordAsync(DocumentRow row, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // One writer per company at a time, so two uploads of the same type cannot both end up current.
        await LockCompanyAsync(db, row.CompanyId, cancellationToken);
        if (row.IsCurrent)
        {
            await ReleaseCurrentAsync(db, row.CompanyId, row.Type, cancellationToken);
        }

        db.Documents.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static Task<int> LockCompanyAsync(VendorsDbContext db, Guid companyId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"select 1 from vendor.companies where id = {companyId} for update", cancellationToken);

    private static Task<int> ReleaseCurrentAsync(VendorsDbContext db, Guid companyId, string type, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"update vendor.documents set is_current = false where company_id = {companyId} and type = {type} and is_current",
            cancellationToken);

    private Task AuditInfectedAsync(Guid companyId, string? actorId, string signature, CancellationToken cancellationToken)
    {
        LogInfected(logger, companyId, signature);
        return platformAudit.WriteAsync(
            new PlatformAuditEntry(actorId, InfectedAction, "vendor_company", companyId.ToString("D"),
                new Dictionary<string, string?> { ["signature"] = signature }),
            cancellationToken);
    }

    private Guid RequireCompany() =>
        vendors.Current?.CompanyId ?? throw new InvalidOperationException("Vendor documents need the vendor context of a signed-in vendor.");

    private static MemoryStream AsStream(ReadOnlyMemory<byte> content) =>
        MemoryMarshal.TryGetArray(content, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(content.ToArray(), writable: false);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vendor document {DocumentId} of company {CompanyId} is waiting in quarantine for its virus scan.")]
    private static partial void LogPending(ILogger logger, Guid companyId, Guid documentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A vendor upload of company {CompanyId} was infected ({Signature}); it was not kept.")]
    private static partial void LogInfected(ILogger logger, Guid companyId, string signature);

    [LoggerMessage(Level = LogLevel.Error, Message = "The quarantined file of vendor document {DocumentId} of company {CompanyId} is missing; it stays pending.")]
    private static partial void LogQuarantineMissing(ILogger logger, Guid companyId, Guid documentId);
}
