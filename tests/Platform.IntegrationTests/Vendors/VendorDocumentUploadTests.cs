using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Documents;
using Platform.Shared;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// Vendor documents over chunked HTTP (F-12, V-8 to V-10, ADR-0001): start, 1 MB chunks each with its SHA-256, complete
/// (assemble, hash, check the file's first bytes, scan with ClamAV, store). Only a clean file is listed; an infected one
/// is never stored and is audited; with the scanner down the file waits in quarantine until the retry job scans it.
/// Everything under the Vendor policy with the antiforgery header, and only for the company that started the upload.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorDocumentUploadTests(DatabaseFixture db, MinioFixture minio, ClamAvFixture clamAv)
    : IClassFixture<MinioFixture>, IClassFixture<ClamAvFixture>
{
    private const int Megabyte = 1024 * 1024;

    private static readonly DateOnly NextYear = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_document_is_listed_only_after_a_clean_scan()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.Pdf((2 * Megabyte) + 5000);

        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 3);
        await uploads.SendAllAsync(uploadId, file);
        (await ListAsync(factory, companyId)).ShouldBeEmpty();

        using var complete = await uploads.CompleteAsync(uploadId, NextYear);

        complete.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Json(complete);
        body.GetProperty("status").GetString().ShouldBe("clean");
        body.GetProperty("sha256").GetString().ShouldBe(VendorDocumentRows.Sha256(file));
        var documentId = body.GetProperty("documentId").GetGuid();

        var listed = (await ListAsync(factory, companyId)).ShouldHaveSingleItem();
        listed.Id.ShouldBe(documentId);
        listed.Type.ShouldBe(VendorDocumentTypes.CrCertificate);
        listed.ExpiresOn.ShouldBe(NextYear);
        listed.Sha256.ShouldBe(VendorDocumentRows.Sha256(file));
        listed.IsCurrent.ShouldBeTrue();
        (await minio.ReadAsync($"vendors/{companyId}/documents/{documentId}", Ct)).ShouldBe(file);
        for (var chunk = 0; chunk < 3; chunk++)
        {
            (await minio.ReadAsync($"staging/{uploadId}/{chunk}", Ct)).ShouldBeNull();
        }

        // Another vendor company never lists it.
        var (_, otherCompany) = await VendorAsync();
        (await ListAsync(factory, otherCompany)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_infected_upload_is_rejected_deleted_and_audited()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.InfectedPdf();

        var uploadId = await uploads.StartAsync(VendorDocumentTypes.VatCertificate, "vat.pdf", file.Length, "application/pdf", expectedChunks: 1);
        await uploads.SendAllAsync(uploadId, file);
        using var complete = await uploads.CompleteAsync(uploadId, NextYear);

        complete.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Json(complete)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.Infected);
        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
        (await ListAsync(factory, companyId)).ShouldBeEmpty();
        (await minio.ReadAsync($"staging/{uploadId}/0", Ct)).ShouldBeNull();

        var audit = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, vendor.Subject, "vendor.upload_infected", Ct)).ShouldHaveSingleItem();
        audit.SubjectType.ShouldBe("vendor_company");
        audit.SubjectId.ShouldBe(companyId.ToString());
        var data = JsonDocument.Parse(audit.Data).RootElement;
        data.EnumerateObject().Select(p => p.Name).ShouldBe(["signature"]);
        data.GetProperty("signature").GetString().ShouldBe("Eicar-Signature");

        // Completing again gives the same answer; nothing is stored the second time either.
        using var again = await uploads.CompleteAsync(uploadId, NextYear);
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_scanner_outage_leaves_the_document_pending_and_the_retry_job_lists_it_later()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.Pdf(300_000);

        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 1);
        await uploads.SendAllAsync(uploadId, file);
        using var complete = await uploads.CompleteAsync(uploadId, NextYear);

        complete.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await Json(complete);
        body.GetProperty("status").GetString().ShouldBe("pending_scan");
        var documentId = body.GetProperty("documentId").GetGuid();
        var pending = (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();
        pending.ScanStatus.ShouldBe("pending_scan");
        pending.IsCurrent.ShouldBeFalse();
        pending.ObjectKey.ShouldBe($"vendors/{companyId}/quarantine/{documentId}");
        (await minio.ReadAsync(pending.ObjectKey, Ct)).ShouldBe(file);
        (await ListAsync(factory, companyId)).ShouldBeEmpty();

        // The worker's retry job, with the scanner back: scanned, moved out of quarantine, current, listed.
        await RunRescanJobAsync(clamAv.Settings);

        var listed = (await ListAsync(factory, companyId)).ShouldHaveSingleItem();
        listed.Id.ShouldBe(documentId);
        listed.IsCurrent.ShouldBeTrue();
        var row = (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();
        row.ScanStatus.ShouldBe("clean");
        row.ObjectKey.ShouldBe($"vendors/{companyId}/documents/{documentId}");
        (await minio.ReadAsync(row.ObjectKey, Ct)).ShouldBe(file);
        (await minio.ReadAsync(pending.ObjectKey, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_pending_file_the_retry_job_finds_infected_is_deleted_marked_and_audited()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.InfectedPdf();
        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 1);
        await uploads.SendAllAsync(uploadId, file);
        using (var complete = await uploads.CompleteAsync(uploadId, NextYear))
        {
            complete.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var quarantined = (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().ObjectKey;

        await RunRescanJobAsync(clamAv.Settings);

        var row = (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();
        row.ScanStatus.ShouldBe("infected");
        row.IsCurrent.ShouldBeFalse();
        (await minio.ReadAsync(quarantined, Ct)).ShouldBeNull();
        (await ListAsync(factory, companyId)).ShouldBeEmpty();
        var audit = (await PlatformAuditsForCompanyAsync(companyId)).ShouldHaveSingleItem();
        JsonDocument.Parse(audit).RootElement.GetProperty("signature").GetString().ShouldBe("Eicar-Signature");
    }

    [Fact]
    public async Task While_the_scanner_is_still_down_the_retry_job_leaves_the_document_pending()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.Pdf(50_000);
        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 1);
        await uploads.SendAllAsync(uploadId, file);
        using (await uploads.CompleteAsync(uploadId, NextYear))
        {
        }

        await RunRescanJobAsync(ScannerDown);

        var row = (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();
        row.ScanStatus.ShouldBe("pending_scan");
        (await minio.ReadAsync(row.ObjectKey, Ct)).ShouldBe(file);
    }

    [Fact]
    public async Task A_resumed_upload_after_a_dropped_connection_completes_with_the_same_hash()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.Pdf((2 * Megabyte) + 12345);
        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 3);

        (await uploads.PutChunkAsync(uploadId, 0, Slice(file, 0))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await uploads.PutChunkAsync(uploadId, 1, Slice(file, 1))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // The connection drops during chunk 2: what arrived is cut short, so it does not match the hash the browser sent.
        var chunk2 = Slice(file, 2);
        using (var cut = await uploads.PutChunkAsync(uploadId, 2, chunk2[..(chunk2.Length / 2)], VendorDocumentRows.Sha256(chunk2)))
        {
            cut.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var early = await uploads.CompleteAsync(uploadId, NextYear))
        {
            early.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(early)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.UploadIncomplete);
        }

        // The browser restarts chunk 2, and resends chunk 1 whose answer it never saw: both replace what was there.
        (await uploads.PutChunkAsync(uploadId, 2, chunk2)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await uploads.PutChunkAsync(uploadId, 1, Slice(file, 1))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var complete = await uploads.CompleteAsync(uploadId, NextYear);

        complete.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Json(complete)).GetProperty("sha256").GetString().ShouldBe(VendorDocumentRows.Sha256(file));
        (await ListAsync(factory, companyId)).ShouldHaveSingleItem().Sha256.ShouldBe(VendorDocumentRows.Sha256(file));

        // Completing again (the browser never saw the answer) returns the same document; a late chunk is refused.
        using var again = await uploads.CompleteAsync(uploadId, NextYear);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Json(again)).GetProperty("documentId").GetGuid().ShouldBe((await ListAsync(factory, companyId)).Single().Id);
        (await uploads.PutChunkAsync(uploadId, 0, Slice(file, 0))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_file_with_the_wrong_type_or_over_10_MB_is_refused()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);

        // Declared too large, or of a type that is not a PDF, PNG or JPEG: refused before a byte is sent.
        using (var large = await uploads.StartRawAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", (10 * Megabyte) + 1, "application/pdf"))
        {
            large.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(large)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.TooLarge);
        }

        using (var word = await uploads.StartRawAsync(VendorDocumentTypes.CrCertificate, "cr.docx", 1000, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"))
        {
            word.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(word)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.WrongType);
        }

        using (var unknownType = await uploads.StartRawAsync("passport", "p.pdf", 1000, "application/pdf"))
        {
            unknownType.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(unknownType)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.UnknownType);
        }

        // Declared as a PDF, but the bytes are not one: refused at completion, nothing stored.
        var html = "<html><body>not a pdf</body></html>"u8.ToArray();
        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", html.Length, "application/pdf", expectedChunks: 1);
        await uploads.SendAllAsync(uploadId, html);
        using (var complete = await uploads.CompleteAsync(uploadId, NextYear))
        {
            complete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(complete)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.WrongType);
        }

        // A chunk body over 1 MB is refused from its Content-Length, before it is read.
        var big = VendorDocumentRows.Pdf(3 * Megabyte);
        var bigId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", big.Length, "application/pdf", expectedChunks: 3);
        using (var oversized = await uploads.PutChunkAsync(bigId, 0, big[..(Megabyte + 1)]))
        {
            oversized.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        }

        // A chunk index past the end is refused.
        using (var pastEnd = await uploads.PutChunkAsync(bigId, 3, big[..100]))
        {
            pastEnd.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_vendor_cannot_touch_an_upload_it_did_not_start()
    {
        var (owner, ownerCompany) = await VendorAsync();
        var (intruder, intruderCompany) = await VendorAsync();
        await using var factory = Factory();
        var ownerUploads = new Uploads(factory, owner);
        var intruderUploads = new Uploads(factory, intruder);
        var file = VendorDocumentRows.Pdf(200_000);
        var uploadId = await ownerUploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 1);

        using (var chunk = await intruderUploads.PutChunkAsync(uploadId, 0, VendorDocumentRows.Pdf(200_000)))
        {
            chunk.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        await ownerUploads.SendAllAsync(uploadId, file);
        using (var complete = await intruderUploads.CompleteAsync(uploadId, NextYear))
        {
            complete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, intruderCompany, Ct)).ShouldBeEmpty();
        using var own = await ownerUploads.CompleteAsync(uploadId, NextYear);
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListAsync(factory, ownerCompany)).ShouldHaveSingleItem().Sha256.ShouldBe(VendorDocumentRows.Sha256(file));
    }

    [Fact]
    public async Task A_new_document_of_the_same_type_replaces_the_current_one_and_keeps_history()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        var first = VendorDocumentRows.Pdf(40_000);
        var second = VendorDocumentRows.Pdf(60_000);
        var vat = VendorDocumentRows.Pdf(30_000);

        var firstId = await uploads.UploadAsync(VendorDocumentTypes.CrCertificate, first, NextYear);
        var vatId = await uploads.UploadAsync(VendorDocumentTypes.VatCertificate, vat, NextYear);
        var secondId = await uploads.UploadAsync(VendorDocumentTypes.CrCertificate, second, NextYear.AddYears(1));

        var listed = await ListAsync(factory, companyId);
        listed.Select(d => d.Id).ShouldBe([firstId, vatId, secondId], ignoreOrder: true);
        listed.Single(d => d.Id == secondId).IsCurrent.ShouldBeTrue();
        listed.Single(d => d.Id == firstId).IsCurrent.ShouldBeFalse();
        listed.Single(d => d.Id == vatId).IsCurrent.ShouldBeTrue();
        (await minio.ReadAsync($"vendors/{companyId}/documents/{firstId}", Ct)).ShouldBe(first);
    }

    [Fact]
    public async Task Upload_requests_without_the_antiforgery_header_or_from_a_non_vendor_are_refused()
    {
        var (vendor, _) = await VendorAsync();
        await using var factory = Factory();
        using var client = AdminRequests.Client(factory, "acme.localhost");
        var start = new { documentType = VendorDocumentTypes.CrCertificate, fileName = "cr.pdf", size = 1000, contentType = "application/pdf" };

        using (var noToken = await client.SendAsync(
                   new HttpRequestMessage(HttpMethod.Post, "/vendor/uploads") { Content = JsonContent.Create(start) }.As(vendor), Ct))
        {
            noToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var staff = new TestUser($"staff-{Guid.NewGuid():N}", ["acme"], "en", Email: "staff@acme.test", EmailVerified: true);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, staff.Subject, $"{staff.Subject}@acme.test", [TenantRoles.TenantAdmin], "active", Ct);
        using (var asStaff = await new Uploads(factory, staff).StartRawAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf"))
        {
            asStaff.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var anonymous = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/vendor/uploads") { Content = JsonContent.Create(start) }, Ct);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static IReadOnlyDictionary<string, string?> ScannerDown => new Dictionary<string, string?>
    {
        ["ClamAv:Host"] = "127.0.0.1",
        ["ClamAv:Port"] = UnusedPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ClamAv:TimeoutSeconds"] = "5",
    };

    private WebApplicationFactory<Program> Factory(bool scannerUp = true) =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in minio.Settings.Concat(scannerUp ? clamAv.Settings : ScannerDown))
            {
                builder.UseSetting(key, value);
            }
        });

    /// <summary>The worker's retry job, run once in a host wired as the worker wires it, with the given scanner.</summary>
    private async Task RunRescanJobAsync(IReadOnlyDictionary<string, string?> scanner)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(minio.Settings.Concat(scanner)).Build();
        await using var host = new ModuleHost(db.AppConnectionString, objectStorage: configuration, configure: services =>
        {
            services.AddVirusScanner(configuration);
            services.AddVendorJobs();
        });
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<VendorDocumentRescanJob>().RunAsync(Ct);
    }

    private static async Task<IReadOnlyList<VendorDocument>> ListAsync(WebApplicationFactory<Program> factory, Guid companyId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<VendorAccessor>().Set(new VendorContext(companyId));
        return await scope.ServiceProvider.GetRequiredService<IVendorDocuments>().ListAsync(Ct);
    }

    private async Task<IReadOnlyList<string>> PlatformAuditsForCompanyAsync(Guid companyId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "select data::text from ops.platform_audit where action = 'vendor.upload_infected' and subject_id = @company", connection);
        command.Parameters.AddWithValue("company", companyId.ToString());
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private async Task<(TestUser User, Guid CompanyId)> VendorAsync()
    {
        var subject = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, subject, VendorRows.NewCrNumber(), "Upload Company", Ct);
        return (new TestUser(subject, ["acme"], "en", RealmRoles: [IdentityClaims.VendorRealmRole], Email: $"{subject}@vendor.test", EmailVerified: true), companyId);
    }

    private static byte[] Slice(byte[] file, int chunk) =>
        file[(chunk * Megabyte)..Math.Min(file.Length, (chunk + 1) * Megabyte)];

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    private static int UnusedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The upload API as the FileUpload component's script calls it: JSON and raw chunks, antiforgery header and cookie.</summary>
    private sealed class Uploads(WebApplicationFactory<Program> factory, TestUser user)
    {
        private readonly HttpClient _client = AdminRequests.Client(factory, "acme.localhost");
        private readonly (string CookieName, string CookieToken, string FormField, string RequestToken) _tokens =
            AdminRequests.AntiforgeryTokens(factory.Services, user);

        public Task<HttpResponseMessage> StartRawAsync(string documentType, string fileName, long size, string contentType) =>
            SendAsync(HttpMethod.Post, "/vendor/uploads", JsonContent.Create(new { documentType, fileName, size, contentType }));

        public async Task<Guid> StartAsync(string documentType, string fileName, long size, string contentType, int expectedChunks)
        {
            using var response = await StartRawAsync(documentType, fileName, size, contentType);
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            var body = await Json(response);
            body.GetProperty("chunkSize").GetInt32().ShouldBe(Megabyte);
            body.GetProperty("chunkCount").GetInt32().ShouldBe(expectedChunks);
            return body.GetProperty("uploadId").GetGuid();
        }

        public Task<HttpResponseMessage> PutChunkAsync(Guid uploadId, int index, byte[] content, string? sha256 = null)
        {
            var body = new ByteArrayContent(content);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return SendAsync(HttpMethod.Put, $"/vendor/uploads/{uploadId}/chunks/{index}", body, sha256 ?? VendorDocumentRows.Sha256(content));
        }

        public async Task SendAllAsync(Guid uploadId, byte[] file)
        {
            for (var index = 0; index * Megabyte < file.Length; index++)
            {
                using var response = await PutChunkAsync(uploadId, index, Slice(file, index));
                response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }
        }

        public Task<HttpResponseMessage> CompleteAsync(Guid uploadId, DateOnly expiresOn) =>
            SendAsync(HttpMethod.Post, $"/vendor/uploads/{uploadId}/complete", JsonContent.Create(new { expiresOn }));

        /// <summary>Start, every chunk, complete; the file must scan clean. Returns the document id.</summary>
        public async Task<Guid> UploadAsync(string documentType, byte[] file, DateOnly expiresOn)
        {
            var uploadId = await StartAsync(documentType, "file.pdf", file.Length, "application/pdf", (file.Length + Megabyte - 1) / Megabyte);
            await SendAllAsync(uploadId, file);
            using var complete = await CompleteAsync(uploadId, expiresOn);
            complete.StatusCode.ShouldBe(HttpStatusCode.OK);
            return (await Json(complete)).GetProperty("documentId").GetGuid();
        }

        private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent content, string? sha256 = null)
        {
            var request = new HttpRequestMessage(method, path) { Content = content }.As(user);
            request.Headers.Add("RequestVerificationToken", _tokens.RequestToken);
            request.Headers.Add("Cookie", $"{_tokens.CookieName}={_tokens.CookieToken}");
            if (sha256 is not null)
            {
                request.Headers.Add("X-Chunk-Sha256", sha256);
            }

            return _client.SendAsync(request, Ct);
        }
    }
}
