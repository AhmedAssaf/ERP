using Platform.Shared.Results;

namespace Platform.Modules.Vendors.Contracts;

/// <summary>A document type a vendor company keeps on file (V-8), named in both languages.</summary>
public sealed record VendorDocumentType(string Code, string NameAr, string NameEn);

/// <summary>The document types of the MVP (V-8): the commercial registration and the VAT registration certificates.</summary>
public static class VendorDocumentTypes
{
    public const string CrCertificate = "cr_certificate";
    public const string VatCertificate = "vat_certificate";

    /// <summary>Every type, in the order pages list them. Each is required for a submission (F-22).</summary>
    public static IReadOnlyList<VendorDocumentType> All { get; } =
    [
        new(CrCertificate, "شهادة السجل التجاري", "Commercial registration certificate"),
        new(VatCertificate, "شهادة التسجيل في ضريبة القيمة المضافة", "VAT registration certificate"),
    ];

    /// <summary>The type with this code, or null when there is none.</summary>
    public static VendorDocumentType? Find(string? code) => All.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.Ordinal));
}

/// <summary>File limits of vendor documents (V-8, V-9).</summary>
public static class VendorDocumentLimits
{
    /// <summary>The largest document: 10 MB.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>Every chunk but the last is exactly this long: 1 MB.</summary>
    public const int ChunkBytes = 1024 * 1024;

    /// <summary>PDF, PNG or JPEG, recognised by the file's first bytes.</summary>
    public static IReadOnlyList<string> ContentTypes { get; } = ["application/pdf", "image/png", "image/jpeg"];
}

/// <summary>Where a new document stands after its upload.</summary>
public enum VendorDocumentStatus
{
    /// <summary>Scanned clean, stored and listed; the current file of its type unless a newer one is.</summary>
    Clean,

    /// <summary>The scanner gave no verdict: kept in quarantine, not listed, scanned again by the worker.</summary>
    PendingScan,
}

/// <summary>A vendor's own document as the vendor sees it: only files that scanned clean.</summary>
public sealed record VendorDocument(
    Guid Id, string Type, DateOnly ExpiresOn, string Sha256, bool IsCurrent, DateTimeOffset UploadedAt);

/// <summary>The outcome of a stored upload: the new document, the SHA-256 of its content, and whether it is listed yet.</summary>
public sealed record VendorDocumentAdded(Guid DocumentId, string Sha256, VendorDocumentStatus Status);

/// <summary>
/// The documents of the current vendor context's company (F-12, V-8, V-10). Works only with a vendor context; the
/// company is always that context's, never a caller's argument.
/// </summary>
public interface IVendorDocuments
{
    /// <summary>Every clean document of the company, current and older (history), newest first.</summary>
    Task<IReadOnlyList<VendorDocument>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the content (a PDF, PNG or JPEG of at most 10 MB, by its first bytes), hashes it, scans it and stores it.
    /// Clean: stored and listed, and the current file of <paramref name="type"/> (the previous current one is kept as
    /// history). Infected: nothing is stored, the finding is audited (<c>vendor.upload_infected</c>, the signature name
    /// only) and <see cref="VendorDocumentErrors.Infected"/> returned. Scanner unavailable: kept in quarantine as
    /// <see cref="VendorDocumentStatus.PendingScan"/>, not listed, until the worker's retry scan decides.
    /// </summary>
    Task<Result<VendorDocumentAdded>> AddAsync(string type, DateOnly expiresOn, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
}

/// <summary>What a vendor declares when an upload starts: the document type and the file's name, size and type.</summary>
public sealed record VendorUploadStart(string? DocumentType, string? FileName, long Size, string? ContentType);

/// <summary>A started upload: send <see cref="ChunkCount"/> chunks of <see cref="ChunkSize"/> bytes (the last may be shorter).</summary>
public sealed record VendorUploadStarted(Guid UploadId, int ChunkSize, int ChunkCount);

/// <summary>
/// Chunked uploads of vendor documents (V-9, ADR-0001): start, chunks by index (a chunk sent again replaces the one
/// before, so a retry after a dropped connection is harmless), complete. Staged in object storage for at most a day.
/// Works only with a vendor context; an upload of another company is not found.
/// </summary>
public interface IVendorUploads
{
    /// <summary>
    /// Starts an upload of a declared file. Refused with <see cref="VendorDocumentErrors.TooManyUploads"/> while the
    /// company already has 10 open uploads (not completed, under a day old).
    /// </summary>
    Task<Result<VendorUploadStarted>> StartAsync(VendorUploadStart start, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores chunk <paramref name="index"/> when its length is the one expected at that index and its SHA-256 is
    /// <paramref name="sha256"/> (hex). Refused once the upload is complete. Returns how many distinct chunks have arrived.
    /// </summary>
    Task<Result<int>> PutChunkAsync(Guid uploadId, int index, ReadOnlyMemory<byte> content, string? sha256, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assembles every chunk in order and checks, scans and stores the file as <see cref="IVendorDocuments.AddAsync"/>
    /// does, recording the document and the upload's outcome together. Completing an upload again returns its first
    /// outcome; while another completion of it is running, <see cref="VendorDocumentErrors.UploadInProgress"/>.
    /// </summary>
    Task<Result<VendorDocumentAdded>> CompleteAsync(Guid uploadId, DateOnly expiresOn, CancellationToken cancellationToken = default);
}

/// <summary>Why a document type blocks a submission.</summary>
public enum BlockingReason
{
    /// <summary>No current file that scanned clean.</summary>
    Missing,

    /// <summary>The current file expired before the date asked about.</summary>
    Expired,
}

/// <summary>A required document type that blocks a submission, named in both languages.</summary>
public sealed record BlockingDocument(string Type, string NameAr, string NameEn, BlockingReason Reason, DateOnly? ExpiredOn);

/// <summary>Whether a vendor company's documents allow a submission (the submission wizard, F-22, asks it).</summary>
public interface IVendorCompliance
{
    /// <summary>
    /// Every required document type without a current clean file, or whose current file expired before
    /// <paramref name="onDate"/> (a file is valid through its expiry date). Asked as the company itself (its vendor
    /// context) or as tenant staff, who see the company's documents only while their tenant has a relationship with it.
    /// Throws <see cref="InvalidOperationException"/> as another company's vendor, or with neither context.
    /// </summary>
    Task<IReadOnlyList<BlockingDocument>> GetBlockingDocumentsAsync(Guid companyId, DateOnly onDate, CancellationToken cancellationToken = default);
}

/// <summary>Stable error codes of vendor documents and uploads; the upload endpoints return them, pages localize them.</summary>
public static class VendorDocumentErrors
{
    public const string UnknownType = "vendor.document_unknown_type";
    public const string WrongType = "vendor.document_wrong_type";
    public const string TooLarge = "vendor.document_too_large";
    public const string Empty = "vendor.document_empty";
    public const string InvalidFileName = "vendor.document_invalid_file_name";
    public const string InvalidExpiry = "vendor.document_invalid_expiry";
    public const string Infected = "vendor.document_infected";
    public const string UploadNotFound = "vendor.upload_not_found";
    public const string UploadIncomplete = "vendor.upload_incomplete";
    public const string UploadCompleted = "vendor.upload_completed";
    public const string UploadInProgress = "vendor.upload_in_progress";
    public const string TooManyUploads = "vendor.too_many_uploads";
    public const string ChunkOutOfRange = "vendor.chunk_out_of_range";
    public const string ChunkWrongSize = "vendor.chunk_wrong_size";
    public const string ChunkHashMismatch = "vendor.chunk_hash_mismatch";
}
