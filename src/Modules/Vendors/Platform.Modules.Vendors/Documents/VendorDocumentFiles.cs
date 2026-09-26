using System.Security.Cryptography;

namespace Platform.Modules.Vendors.Documents;

/// <summary>
/// File rules of vendor documents (V-8): the type comes from the first bytes, never from the name or a declared type,
/// and object keys keep every company's files under its own prefix. Chunks are staged by upload id; the upload row, under
/// the company policy, is what ties an upload to its company.
/// </summary>
internal static class VendorDocumentFiles
{
    public const string Pdf = "application/pdf";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    private static ReadOnlySpan<byte> PdfMagic => "%PDF-"u8;

    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];

    /// <summary>The content type the first bytes show (PDF, PNG or JPEG), or null for anything else.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> head) =>
        head.StartsWith(PdfMagic) ? Pdf
        : head.StartsWith(PngMagic) ? Png
        : head.StartsWith(JpegMagic) ? Jpeg
        : null;

    /// <summary>Where a clean document lives.</summary>
    public static string DocumentKey(Guid companyId, Guid documentId) => $"vendors/{companyId:D}/documents/{documentId:D}";

    /// <summary>Where a document waits for its scan; never served.</summary>
    public static string QuarantineKey(Guid companyId, Guid documentId) => $"vendors/{companyId:D}/quarantine/{documentId:D}";

    /// <summary>Where chunk <paramref name="index"/> of an upload is staged until completion or cleanup.</summary>
    public static string ChunkKey(Guid uploadId, int index) => $"staging/{uploadId:D}/{index}";

    public static string Sha256Hex(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// A private temporary file for one document while it is assembled, scanned and stored, so a 10 MB file never sits in
    /// one array; the file is deleted when the stream is disposed.
    /// </summary>
    public static FileStream CreateTempFile() => new(
        Path.Combine(Path.GetTempPath(), $"waslabid-vendor-{Guid.NewGuid():N}.tmp"),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize: 81920,
        FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    /// <summary>Reads the first bytes of a seekable stream (at most <paramref name="count"/>) and rewinds it.</summary>
    public static async Task<byte[]> ReadHeadAsync(Stream content, int count, CancellationToken cancellationToken)
    {
        content.Position = 0;
        var head = new byte[count];
        var read = await content.ReadAtLeastAsync(head, count, throwOnEndOfStream: false, cancellationToken);
        content.Position = 0;
        return head[..read];
    }
}
