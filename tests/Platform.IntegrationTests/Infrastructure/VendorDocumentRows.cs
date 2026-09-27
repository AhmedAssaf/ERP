using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>A vendor document row as the owner reads it, bypassing row-level security.</summary>
internal sealed record VendorDocumentRow(
    Guid Id, string Type, DateOnly ExpiresOn, string ObjectKey, string Sha256, string ScanStatus, bool IsCurrent);

/// <summary>Vendor documents and uploads for tests (vendor plan task 3): owner reads and writes, and test files.</summary>
internal static class VendorDocumentRows
{
    public static async Task<IReadOnlyList<VendorDocumentRow>> ForCompanyAsync(
        string ownerConnectionString, Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select id, type, expires_on, object_key, sha256, scan_status, is_current
            from vendor.documents where company_id = @company order by created_at
            """, connection);
        command.Parameters.AddWithValue("company", companyId);
        var rows = new List<VendorDocumentRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new VendorDocumentRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetBoolean(6)));
        }

        return rows;
    }

    /// <summary>A document row written directly as the owner (the compliance tests need rows, not files).</summary>
    public static async Task InsertAsync(
        string ownerConnectionString, Guid companyId, string type, DateOnly expiresOn, string scanStatus, bool isCurrent,
        CancellationToken cancellationToken, DateTimeOffset? createdAt = null)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            insert into vendor.documents (id, company_id, type, expires_on, object_key, sha256, scan_status, is_current, created_at)
            values (@id, @company, @type, @expires, @key, @sha, @status, @current, @created)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("expires", expiresOn);
        command.Parameters.AddWithValue("key", $"vendors/{companyId}/documents/{id}");
        command.Parameters.AddWithValue("sha", Convert.ToHexStringLower(SHA256.HashData(id.ToByteArray())));
        command.Parameters.AddWithValue("status", scanStatus);
        command.Parameters.AddWithValue("current", isCurrent);
        command.Parameters.AddWithValue("created", createdAt ?? DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>How often the retry job tried to scan the document, and when it last did.</summary>
    public static async Task<(int Attempts, DateTimeOffset? LastScanAt)> ScanAttemptsAsync(
        string ownerConnectionString, Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select scan_attempts, last_scan_at from vendor.documents where id = @id", connection);
        command.Parameters.AddWithValue("id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        (await reader.ReadAsync(cancellationToken)).ShouldBeTrue();
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    /// <summary>The documents the retry job would scan next, as <c>vendor.pending_scan_documents</c> lists them.</summary>
    public static async Task<IReadOnlyList<Guid>> PendingScanListAsync(string ownerConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select id from vendor.pending_scan_documents(1000)", connection);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>The upload row's id, or null when it is gone.</summary>
    public static async Task<bool> UploadExistsAsync(string ownerConnectionString, Guid uploadId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select count(*)::int from vendor.uploads where id = @id", connection);
        command.Parameters.AddWithValue("id", uploadId);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))! == 1;
    }

    /// <summary>Puts a pending document back among the untried ones (no <c>last_scan_at</c>), keeping its attempts.</summary>
    public static async Task ResetQueuePositionAsync(string ownerConnectionString, Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("update vendor.documents set last_scan_at = null where id = @id", connection);
        command.Parameters.AddWithValue("id", documentId);
        (await command.ExecuteNonQueryAsync(cancellationToken)).ShouldBe(1);
    }

    /// <summary>Every pending document's retry attempts, ordered by id.</summary>
    public static async Task<IReadOnlyList<string>> PendingAttemptsAsync(string ownerConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select id::text || '=' || scan_attempts from vendor.documents where scan_status = 'pending_scan' order by id", connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>The upload's recorded outcome, or null while it is open.</summary>
    public static async Task<string?> UploadOutcomeAsync(string ownerConnectionString, Guid uploadId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select outcome from vendor.uploads where id = @id", connection);
        command.Parameters.AddWithValue("id", uploadId);
        var outcome = await command.ExecuteScalarAsync(cancellationToken);
        outcome.ShouldNotBeNull("the upload exists");
        return outcome as string;
    }

    /// <summary>What a platform operator runs as the owner role for a parked document (docs/07 section 4).</summary>
    public static async Task<bool> UnparkAsync(string ownerConnectionString, Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select vendor.unpark_document(@id)", connection);
        command.Parameters.AddWithValue("id", documentId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Moves the upload's start into the past, as if it had been abandoned that long ago.</summary>
    public static async Task AgeUploadAsync(string ownerConnectionString, Guid uploadId, TimeSpan age, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("update vendor.uploads set created_at = now() - @age where id = @id", connection);
        command.Parameters.AddWithValue("id", uploadId);
        command.Parameters.AddWithValue("age", age);
        (await command.ExecuteNonQueryAsync(cancellationToken)).ShouldBe(1);
    }

    /// <summary>
    /// A well-formed PDF of about <paramref name="size"/> bytes: one page and an embedded file carrying random bytes (or
    /// <paramref name="payload"/>). ClamAV opens PDFs and scans their embedded files, which is how the infected case works.
    /// </summary>
    public static byte[] Pdf(int size, byte[]? payload = null)
    {
        payload ??= RandomNumberGenerator.GetBytes(Math.Max(0, size - 700));
        string[] heads =
        [
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(attachment.bin) 4 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Filespec /F (attachment.bin) /EF << /F 5 0 R >> >>",
        ];
        using var output = new MemoryStream();
        var offsets = new List<long>();
        Write(output, "%PDF-1.4\n");
        for (var i = 0; i < heads.Length; i++)
        {
            offsets.Add(output.Position);
            Write(output, $"{i + 1} 0 obj\n{heads[i]}\nendobj\n");
        }

        offsets.Add(output.Position);
        Write(output, $"5 0 obj\n<< /Type /EmbeddedFile /Length {payload.Length} >>\nstream\n");
        output.Write(payload);
        Write(output, "\nendstream\nendobj\n");
        var xref = output.Position;
        Write(output, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write(output, $"{offset:D10} 00000 n \n");
        }

        Write(output, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>
    /// A PDF whose embedded file is the EICAR anti-virus test string. The string is kept base64-encoded and decoded only
    /// in memory, so neither the source file nor the test assembly trips the developer machine's own virus scanner.
    /// </summary>
    public static byte[] InfectedPdf() => Pdf(0, Convert.FromBase64String(
        "WDVPIVAlQEFQWzRcUFpYNTQoUF4pN0NDKTd9JEVJQ0FSLVNUQU5EQVJELUFOVElWSVJVUy1URVNULUZJTEUhJEgrSCo="));

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static void Write(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));
}
