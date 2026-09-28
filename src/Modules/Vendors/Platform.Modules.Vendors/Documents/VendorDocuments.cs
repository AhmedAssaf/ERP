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
/// company, data the signature name and the upload or document id): the file belongs to the platform-level company, not
/// to the tenant whose host it came through, and the retry scan has no tenant at all. The audit is written before the
/// finding is recorded, so it is never lost; the id makes a repeated entry recognisable.
/// <para>
/// Adding a document has two halves: <see cref="ScanAndStoreAsync"/> checks, scans and stores the file, and
/// <see cref="RecordAsync"/> adds its row inside the caller's transaction, so a completed upload writes the document and the
/// upload's outcome together (<see cref="VendorUploads"/>). Once the scan has answered, everything after it runs with
/// <see cref="CancellationToken.None"/>: a caller that goes away does not leave a scanned file half recorded.
/// </para>
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

    /// <summary>The platform audit action when a document reaches <see cref="MaxScanAttempts"/> and is parked.</summary>
    public const string ParkedAction = "vendor.document_parked";

    /// <summary>
    /// Retry scans without a verdict after which a document is parked for a person (V-10): it stays pending, and
    /// <c>vendor.pending_scan_documents</c> no longer lists it. The same number is in Migrations/0006.
    /// </summary>
    public const int MaxScanAttempts = 12;

    private const string Clean = "clean";
    private const string PendingScan = "pending_scan";
    private const string Infected = "infected";

    /// <summary>Enough of the file's start to tell a PDF, PNG or JPEG.</summary>
    private const int HeadBytes = 16;

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

    public async Task<IReadOnlyList<VendorDocumentNotClean>> ListNotCleanAsync(CancellationToken cancellationToken = default)
    {
        var companyId = RequireCompany();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ScanStatus != Clean)
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Select(d => new { d.Id, d.Type, d.ExpiresOn, d.ScanStatus, d.CreatedAt })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(d => new VendorDocumentNotClean(
            d.Id, d.Type, d.ExpiresOn, d.ScanStatus == Infected ? VendorDocumentScanState.Infected : VendorDocumentScanState.PendingScan, d.CreatedAt))];
    }

    public async Task<Result<VendorDocumentAdded>> AddAsync(
        string type, DateOnly expiresOn, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var companyId = RequireCompany();
        await using var file = AsStream(content);
        var scanned = await ScanAndStoreAsync(
            Guid.CreateVersion7(), type, expiresOn, file, VendorDocumentFiles.Sha256Hex(content.Span), cancellationToken);
        if (!scanned.IsSuccess)
        {
            return Result.Failure<VendorDocumentAdded>(scanned.Error);
        }

        if (scanned.Value.Signature is { } signature)
        {
            // Nothing was stored and nothing is recorded: the signature is all there is to audit.
            await AuditInfectedAsync(companyId, actingUser.UserId, signature, null, CancellationToken.None);
            return InfectedResult();
        }

        var row = scanned.Value.Document!;
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await RecordAsync(db, row);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return Result.Success(Added(row));
    }

    /// <summary>
    /// Checks the file (type from its first bytes, size, expiry), scans it and, unless infected, stores it under
    /// <paramref name="documentId"/>: clean at its document key, otherwise (no verdict) in quarantine. Nothing is recorded;
    /// the caller adds <see cref="ScannedDocument.Document"/> with <see cref="RecordAsync"/>, or audits
    /// <see cref="ScannedDocument.Signature"/> with <see cref="AuditUploadInfectedAsync"/> before it commits the outcome.
    /// An infected file is never stored, not even in quarantine. <paramref name="content"/> must be seekable; storing it
    /// is not cancelled once the scanner has answered.
    /// </summary>
    internal async Task<Result<ScannedDocument>> ScanAndStoreAsync(
        Guid documentId, string type, DateOnly expiresOn, Stream content, string sha256, CancellationToken cancellationToken)
    {
        var companyId = RequireCompany();
        var head = await VendorDocumentFiles.ReadHeadAsync(content, HeadBytes, cancellationToken);
        if (Check(type, expiresOn, content.Length, head) is { } refused)
        {
            return Result.Failure<ScannedDocument>(refused);
        }

        var contentType = VendorDocumentFiles.DetectContentType(head)!;
        var scan = await scanner.ScanAsync(content, cancellationToken);
        if (scan.Verdict == ScanVerdict.Infected)
        {
            LogInfected(logger, companyId, scan.Signature!);
            return Result.Success(new ScannedDocument(null, scan.Signature));
        }

        var clean = scan.Verdict == ScanVerdict.Clean;
        var key = clean ? VendorDocumentFiles.DocumentKey(companyId, documentId) : VendorDocumentFiles.QuarantineKey(companyId, documentId);
        content.Position = 0;
        await storage.PutAsync(key, content, contentType, CancellationToken.None);
        if (!clean)
        {
            LogPending(logger, companyId, documentId);
        }

        return Result.Success(new ScannedDocument(
            new DocumentRow
            {
                Id = documentId,
                CompanyId = companyId,
                Type = type,
                ExpiresOn = expiresOn,
                ObjectKey = key,
                Sha256 = sha256,
                ScanStatus = clean ? Clean : PendingScan,
                IsCurrent = clean,
            },
            null));
    }

    /// <summary>
    /// Adds the document row inside the caller's open transaction on <paramref name="db"/>: locks the company (one writer
    /// at a time, so two uploads of the same type cannot both end up current), releases the current file of the type when
    /// this one becomes current, and adds the row for the caller's <c>SaveChanges</c>.
    /// </summary>
    internal static async Task RecordAsync(VendorsDbContext db, DocumentRow row)
    {
        await LockCompanyAsync(db, row.CompanyId, CancellationToken.None);
        if (row.IsCurrent)
        {
            await ReleaseCurrentAsync(db, row.CompanyId, row.Type, CancellationToken.None);
        }

        db.Documents.Add(row);
    }

    /// <summary>Audits a finding on a vendor's upload, with the signed-in user as the actor and the upload's id.</summary>
    internal Task AuditUploadInfectedAsync(Guid companyId, Guid uploadId, string signature) =>
        AuditInfectedAsync(companyId, actingUser.UserId, signature, ("upload_id", uploadId), CancellationToken.None);

    internal static VendorDocumentAdded Added(DocumentRow row) =>
        new(row.Id, row.Sha256, row.ScanStatus == Clean ? VendorDocumentStatus.Clean : VendorDocumentStatus.PendingScan);

    internal static Result<VendorDocumentAdded> InfectedResult() =>
        Result.Failure<VendorDocumentAdded>(Error.Refused(VendorDocumentErrors.Infected, "The file contains a virus and was deleted."));

    /// <summary>
    /// The worker's retry scan of one pending document of the vendor context's company. Clean: moved out of quarantine
    /// and made current unless a newer file of its type already is. Infected: the finding is audited first, then the row
    /// is marked <c>infected</c> (never listed), then the file is deleted; a failure between the audit and the mark only
    /// means the next run audits again. A quarantined file that is gone is a verdict about the file and counts an attempt
    /// at once (after <see cref="MaxScanAttempts"/> the document is parked for a person, audited). No verdict
    /// (<see cref="ScanVerdict.Failed"/>, <see cref="ScanVerdict.Unavailable"/> or a scanner exception) and a storage
    /// failure while reading the quarantined file move the document to the back of the queue (<c>last_scan_at</c>)
    /// without counting: the job probes the scanner with a known-clean canary to tell the file's fault from an outage
    /// (<see cref="VendorDocumentRescanJob"/>) and charges the file with <see cref="ChargeAttemptAsync"/>.
    /// </summary>
    public async Task<RescanOutcome> RescanAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var companyId = RequireCompany();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Documents.SingleOrDefaultAsync(d => d.Id == documentId && d.ScanStatus == PendingScan, cancellationToken);
        if (row is null)
        {
            return RescanOutcome.NotPending;
        }

        await using var file = VendorDocumentFiles.CreateTempFile();
        string contentType;
        try
        {
            await using var stored = await storage.OpenAsync(row.ObjectKey, cancellationToken);
            if (stored is null)
            {
                LogQuarantineMissing(logger, companyId, documentId);
                await CountAttemptAsync(db, companyId, documentId, touch: true);
                return RescanOutcome.FileMissing;
            }

            await stored.Content.CopyToAsync(file, cancellationToken);
            contentType = stored.ContentType;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Storage could not be read: not the file's fault. Rotated, never charged.
            LogStorageUnavailable(logger, companyId, documentId, ex.GetType().Name);
            await TouchAsync(db, documentId);
            return RescanOutcome.StorageUnavailable;
        }

        file.Position = 0;
        ScanResult scan;
        try
        {
            scan = await scanner.ScanAsync(file, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The scanner threw on this file (it logs no content): no verdict, left to the job's canary probe.
            LogScannerThrew(logger, companyId, documentId, ex.GetType().Name);
            scan = ScanResult.Failed;
        }
        switch (scan.Verdict)
        {
            case ScanVerdict.Clean:
                {
                    var quarantineKey = row.ObjectKey;
                    var key = VendorDocumentFiles.DocumentKey(companyId, documentId);
                    file.Position = 0;
                    await storage.PutAsync(key, file, contentType, CancellationToken.None);
                    await using (var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None))
                    {
                        await LockCompanyAsync(db, companyId, CancellationToken.None);
                        // A file uploaded later may have become current while this one waited; it stays current.
                        var newerCurrent = await db.Documents.AnyAsync(
                            d => d.Type == row.Type && d.IsCurrent && d.CreatedAt > row.CreatedAt, CancellationToken.None);
                        if (!newerCurrent)
                        {
                            await ReleaseCurrentAsync(db, companyId, row.Type, CancellationToken.None);
                        }

                        row.ScanStatus = Clean;
                        row.ObjectKey = key;
                        row.IsCurrent = !newerCurrent;
                        await db.SaveChangesAsync(CancellationToken.None);
                        await transaction.CommitAsync(CancellationToken.None);
                    }

                    await storage.DeleteAsync(quarantineKey, CancellationToken.None);
                    return RescanOutcome.Clean;
                }

            case ScanVerdict.Infected:
                // Audit, mark, delete, in that order: the finding is never recorded without its audit, and a failed delete
                // never leaves an infected file that looks pending.
                await AuditInfectedAsync(companyId, actorId: null, scan.Signature!, ("document_id", documentId), CancellationToken.None);
                row.ScanStatus = Infected;
                await db.SaveChangesAsync(CancellationToken.None);
                try
                {
                    await storage.DeleteAsync(row.ObjectKey, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogInfectedNotDeleted(logger, companyId, documentId, ex.GetType().Name);
                }

                return RescanOutcome.Infected;

            case ScanVerdict.Failed:
                await TouchAsync(db, documentId);
                return RescanOutcome.ScannerFailed;

            default:
                await TouchAsync(db, documentId);
                return RescanOutcome.ScannerUnavailable;
        }
    }

    /// <summary>
    /// Charges one retry attempt to a document of the vendor context's company that got no verdict while the scanner
    /// answered a known-clean canary (so it was the file, not an outage). <c>last_scan_at</c> was set when it was tried.
    /// </summary>
    internal async Task ChargeAttemptAsync(Guid documentId)
    {
        var companyId = RequireCompany();
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await CountAttemptAsync(db, companyId, documentId, touch: false);
    }

    /// <summary>Moves a pending document of the vendor context's company to the back of the retry queue, counting nothing.</summary>
    internal async Task TouchAsync(Guid documentId)
    {
        RequireCompany();
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await TouchAsync(db, documentId);
    }

    /// <summary>The refusal for a file that is not a document of the given type, or null when it may be scanned.</summary>
    internal static Error? Check(string type, DateOnly expiresOn, long length, ReadOnlySpan<byte> head)
    {
        if (VendorDocumentTypes.Find(type) is null)
        {
            return Error.Validation(VendorDocumentErrors.UnknownType, "Choose a commercial registration or VAT certificate.");
        }

        if (length < 1)
        {
            return Error.Validation(VendorDocumentErrors.Empty, "The file is empty.");
        }

        if (length > VendorDocumentLimits.MaxBytes)
        {
            return Error.Validation(VendorDocumentErrors.TooLarge, "The file is larger than 10 MB.");
        }

        if (VendorDocumentFiles.DetectContentType(head) is null)
        {
            return Error.Validation(VendorDocumentErrors.WrongType, "The file is not a PDF, PNG or JPEG.");
        }

        return expiresOn < EarliestExpiry || expiresOn > LatestExpiry
            ? Error.Validation(VendorDocumentErrors.InvalidExpiry, "Enter the document's expiry date.")
            : null;
    }

    /// <summary>
    /// One more attempt (and, with <paramref name="touch"/>, the database's time as <c>last_scan_at</c>). The attempt that
    /// parks the document is audited (<see cref="ParkedAction"/>) before it commits, so a failed audit leaves the count as
    /// it was and the next attempt parks and audits again.
    /// </summary>
    private async Task CountAttemptAsync(VendorsDbContext db, Guid companyId, Guid documentId, bool touch)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var attempts = await db.Database.SqlQuery<int>($"""
            update vendor.documents
            set scan_attempts = scan_attempts + 1, last_scan_at = case when {touch} then now() else last_scan_at end
            where id = {documentId} and scan_status = 'pending_scan' and scan_attempts < {MaxScanAttempts}
            returning scan_attempts as "Value"
            """).ToListAsync(CancellationToken.None);
        if (attempts is [MaxScanAttempts])
        {
            LogParked(logger, companyId, documentId, MaxScanAttempts);
            await platformAudit.WriteAsync(
                new PlatformAuditEntry(null, ParkedAction, "vendor_company", companyId.ToString("D"),
                    new Dictionary<string, string?> { ["document_id"] = documentId.ToString("D") }),
                CancellationToken.None);
        }

        await transaction.CommitAsync(CancellationToken.None);
    }

    private static Task<int> TouchAsync(VendorsDbContext db, Guid documentId) =>
        db.Database.ExecuteSqlAsync(
            $"update vendor.documents set last_scan_at = now() where id = {documentId} and scan_status = 'pending_scan'",
            CancellationToken.None);

    private static Task<int> LockCompanyAsync(VendorsDbContext db, Guid companyId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync($"select 1 from vendor.companies where id = {companyId} for update", cancellationToken);

    private static Task<int> ReleaseCurrentAsync(VendorsDbContext db, Guid companyId, string type, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"update vendor.documents set is_current = false where company_id = {companyId} and type = {type} and is_current",
            cancellationToken);

    private Task AuditInfectedAsync(
        Guid companyId, string? actorId, string signature, (string Name, Guid Value)? id, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, string?> { ["signature"] = signature };
        if (id is { } entry)
        {
            data[entry.Name] = entry.Value.ToString("D");
        }

        return platformAudit.WriteAsync(new PlatformAuditEntry(actorId, InfectedAction, "vendor_company", companyId.ToString("D"), data), cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The quarantined file of vendor document {DocumentId} of company {CompanyId} could not be read ({ErrorType}); it stays pending and is not charged.")]
    private static partial void LogStorageUnavailable(ILogger logger, Guid companyId, Guid documentId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The virus scanner threw on vendor document {DocumentId} of company {CompanyId} ({ErrorType}); treated as no verdict.")]
    private static partial void LogScannerThrew(ILogger logger, Guid companyId, Guid documentId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The quarantined file of vendor document {DocumentId} of company {CompanyId} is missing; it stays pending.")]
    private static partial void LogQuarantineMissing(ILogger logger, Guid companyId, Guid documentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Vendor document {DocumentId} of company {CompanyId} had no scan verdict after {Attempts} attempts; it is parked for a person and no longer retried.")]
    private static partial void LogParked(ILogger logger, Guid companyId, Guid documentId, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "The infected file of vendor document {DocumentId} of company {CompanyId} is marked and audited but could not be deleted ({ErrorType}); delete it from quarantine by hand.")]
    private static partial void LogInfectedNotDeleted(ILogger logger, Guid companyId, Guid documentId, string errorType);
}

/// <summary>
/// A scanned file: <see cref="Document"/>, stored and ready to record (clean, or pending in quarantine), or the
/// <see cref="Signature"/> the scanner found (nothing stored).
/// </summary>
internal sealed record ScannedDocument(DocumentRow? Document, string? Signature);

/// <summary>What one retry scan did (<see cref="VendorDocuments.RescanAsync"/>).</summary>
internal enum RescanOutcome
{
    /// <summary>The document is no longer pending; nothing was done.</summary>
    NotPending,

    Clean,

    Infected,

    /// <summary>clamd answered with an error for this file, or the scanner threw; nothing counted yet.</summary>
    ScannerFailed,

    /// <summary>The quarantined file is gone; an attempt was counted.</summary>
    FileMissing,

    /// <summary>clamd could not be reached or gave no verdict; nothing counted yet.</summary>
    ScannerUnavailable,

    /// <summary>The quarantined file could not be read (storage outage); never charged.</summary>
    StorageUnavailable,
}
