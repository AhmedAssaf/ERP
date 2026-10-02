using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Documents;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared;
using Platform.Shared.Results;
using Platform.Shared.Scanning;
using Platform.Shared.Storage;
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
        // The upload id lets a reader recognise a duplicate: the audit is written before the outcome commits.
        data.EnumerateObject().Select(p => p.Name).ShouldBe(["signature", "upload_id"], ignoreOrder: true);
        data.GetProperty("signature").GetString().ShouldBe("Eicar-Signature");
        data.GetProperty("upload_id").GetString().ShouldBe(uploadId.ToString("D"));

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
        var audit = JsonDocument.Parse((await PlatformAuditsForCompanyAsync(companyId)).ShouldHaveSingleItem()).RootElement;
        audit.GetProperty("signature").GetString().ShouldBe("Eicar-Signature");
        audit.GetProperty("document_id").GetString().ShouldBe(row.Id.ToString("D"));
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
        (await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, row.Id, Ct)).Attempts.ShouldBe(0);
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

    [Fact]
    public async Task A_full_length_chunk_with_a_wrong_or_missing_hash_is_refused_as_damaged()
    {
        var (vendor, _) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.Pdf(Megabyte + 1000);
        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 2);
        var chunk = Slice(file, 0);

        // The right length, so only the hash can tell the chunk is not the one the browser sent.
        using (var wrong = await uploads.PutChunkAsync(uploadId, 0, chunk, VendorDocumentRows.Sha256(Slice(file, 1))))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(wrong)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.ChunkHashMismatch);
        }

        using (var missing = await uploads.PutChunkAsync(uploadId, 0, chunk, withHash: false))
        {
            missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(missing)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.ChunkHashMismatch);
        }

        (await minio.ReadAsync($"staging/{uploadId}/0", Ct)).ShouldBeNull();
        (await uploads.PutChunkAsync(uploadId, 0, chunk)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Two_parallel_completions_store_one_document()
    {
        var (vendor, companyId) = await VendorAsync();
        var gate = new GatedScanner();
        await using var factory = Factory(services: services => services.Replace(ServiceDescriptor.Singleton<IVirusScanner>(gate)));
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.Pdf((3 * Megabyte) + 777);
        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 4);
        await uploads.SendAllAsync(uploadId, file);

        var first = uploads.CompleteAsync(uploadId, NextYear);
        try
        {
            // The first completion holds the upload's row lock while the scanner works: the second one overlaps it for sure.
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            using var second = await uploads.CompleteAsync(uploadId, NextYear);
            second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Json(second)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.UploadInProgress);
        }
        finally
        {
            gate.Release();
        }

        using var completed = await first;
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var documentId = (await Json(completed)).GetProperty("documentId").GetGuid();
        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().Id.ShouldBe(documentId);
    }

    [Fact]
    public async Task Chunk_and_complete_requests_without_the_antiforgery_token_are_refused()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.Pdf(80_000);
        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 1);

        using (var chunk = await uploads.PutChunkAsync(uploadId, 0, file, withAntiforgery: false))
        {
            chunk.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await minio.ReadAsync($"staging/{uploadId}/0", Ct)).ShouldBeNull();
        await uploads.SendAllAsync(uploadId, file);
        using (var complete = await uploads.CompleteAsync(uploadId, NextYear, withAntiforgery: false))
        {
            complete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_eleventh_open_upload_of_a_company_is_refused_with_429()
    {
        var (vendor, _) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        // A completed upload is not open.
        await uploads.UploadAsync(VendorDocumentTypes.CrCertificate, VendorDocumentRows.Pdf(20_000), NextYear);
        var open = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            open.Add(await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf", expectedChunks: 1));
        }

        using (var eleventh = await uploads.StartRawAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf"))
        {
            eleventh.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (await Json(eleventh)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.TooManyUploads);
        }

        // Another company is not limited by this one; an upload older than a day is no longer open.
        var (other, _) = await VendorAsync();
        await new Uploads(factory, other).StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf", expectedChunks: 1);
        await VendorDocumentRows.AgeUploadAsync(db.OwnerConnectionString, open[0], TimeSpan.FromHours(25), Ct);
        await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf", expectedChunks: 1);
    }

    [Fact]
    public async Task Upload_requests_over_the_per_company_rate_limit_get_429()
    {
        var (vendor, _) = await VendorAsync();
        var (other, _) = await VendorAsync();
        await using var factory = Factory(settings: new Dictionary<string, string?> { ["Vendors:UploadRequestsPerMinute"] = "3" });
        var uploads = new Uploads(factory, vendor);

        for (var i = 0; i < 3; i++)
        {
            await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf", expectedChunks: 1);
        }

        using (var limited = await uploads.StartRawAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf"))
        {
            limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        }

        await new Uploads(factory, other).StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf", expectedChunks: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failure_after_the_scan_then_completing_again_leaves_one_outcome(bool infected)
    {
        var (vendor, companyId) = await VendorAsync();
        var failOnce = new FailFirstOutcomeUpdate();
        await using var host = ServiceHost(clamAv.Settings, services =>
            services.ConfigureDbContext<VendorsDbContext>((_, options) => options.AddInterceptors(failOnce)));
        var file = infected ? VendorDocumentRows.InfectedPdf() : VendorDocumentRows.Pdf(150_000);
        var uploadId = await StartAndSendAsync(host, companyId, vendor.Subject, file);

        await Should.ThrowAsync<Exception>(() => CompleteThroughServiceAsync(host, companyId, vendor.Subject, uploadId, Ct));
        failOnce.Failed.ShouldBeTrue();

        var again = await CompleteThroughServiceAsync(host, companyId, vendor.Subject, uploadId, Ct);
        var rows = await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct);
        var audits = await PlatformAuditsForCompanyAsync(companyId);
        if (infected)
        {
            again.Error.ShouldNotBeNull().Code.ShouldBe(VendorDocumentErrors.Infected);
            rows.ShouldBeEmpty();
            // At least once: the audit precedes the commit. At most once per successful commit: one commit here.
            var forUpload = audits.Select(a => JsonDocument.Parse(a).RootElement.GetProperty("upload_id").GetString()).ToList();
            forUpload.ShouldAllBe(id => id == uploadId.ToString("D"));
            forUpload.Count.ShouldBeInRange(1, 1);
        }
        else
        {
            again.IsSuccess.ShouldBeTrue();
            rows.ShouldHaveSingleItem().Id.ShouldBe(again.Value.DocumentId);
            audits.ShouldBeEmpty();
        }

        // And once more: the first recorded outcome, nothing added.
        var third = await CompleteThroughServiceAsync(host, companyId, vendor.Subject, uploadId, Ct);
        third.IsSuccess.ShouldBe(!infected);
        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Count.ShouldBe(rows.Count);
        (await PlatformAuditsForCompanyAsync(companyId)).Count.ShouldBe(audits.Count);
    }

    [Fact]
    public async Task A_request_aborted_after_the_scan_still_records_the_document_and_the_outcome()
    {
        var (vendor, companyId) = await VendorAsync();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var host = ServiceHost(clamAv.Settings, services =>
            services.Replace(ServiceDescriptor.Singleton<IVirusScanner>(new CancelAfterScan(RealScanner(), abort))));
        var file = VendorDocumentRows.Pdf(120_000);
        var uploadId = await StartAndSendAsync(host, companyId, vendor.Subject, file);

        var completed = await CompleteThroughServiceAsync(host, companyId, vendor.Subject, uploadId, abort.Token);

        abort.IsCancellationRequested.ShouldBeTrue();
        completed.IsSuccess.ShouldBeTrue();
        var row = (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();
        row.Id.ShouldBe(completed.Value.DocumentId);
        (await minio.ReadAsync(row.ObjectKey, Ct)).ShouldBe(file);
        var again = await CompleteThroughServiceAsync(host, companyId, vendor.Subject, uploadId, Ct);
        again.Value.DocumentId.ShouldBe(row.Id);
    }

    [Fact]
    public async Task A_file_the_scanner_errors_on_does_not_block_a_later_clean_pending_document()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var uploads = new Uploads(factory, vendor);
        var bad = VendorDocumentRows.Pdf(30_000);
        var good = VendorDocumentRows.Pdf(31_000);
        var badId = await PendingAsync(uploads, VendorDocumentTypes.CrCertificate, bad);
        var goodId = await PendingAsync(uploads, VendorDocumentTypes.VatCertificate, good);

        await RunRescanJobAsync(new ScriptedScanner(content => content.AsSpan().SequenceEqual(bad) ? ScanResult.Failed : ScanResult.Clean));

        var rows = await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct);
        rows.Single(r => r.Id == badId).ScanStatus.ShouldBe("pending_scan");
        rows.Single(r => r.Id == goodId).ScanStatus.ShouldBe("clean");
        var (attempts, lastScanAt) = await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, badId, Ct);
        attempts.ShouldBe(1);
        lastScanAt.ShouldNotBeNull();
        (await VendorDocumentRows.PendingScanListAsync(db.OwnerConnectionString, Ct)).ShouldContain(badId);
    }

    [Fact]
    public async Task A_poison_file_among_clean_files_is_charged_each_run_and_parked_after_twelve()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var uploads = new Uploads(factory, vendor);
        var poison = VendorDocumentRows.Pdf(32_000);
        await PendingAsync(uploads, VendorDocumentTypes.VatCertificate, VendorDocumentRows.Pdf(32_100));
        var poisonId = await PendingAsync(uploads, VendorDocumentTypes.CrCertificate, poison);
        await PendingAsync(uploads, VendorDocumentTypes.VatCertificate, VendorDocumentRows.Pdf(32_200));
        // clamd errors on the poison file only; the canary and every other file scan clean.
        var scanner = new ScriptedScanner(content => content.AsSpan().SequenceEqual(poison) ? ScanResult.Failed : ScanResult.Clean);

        for (var run = 1; run <= 12; run++)
        {
            if (run == 12)
            {
                (await VendorDocumentRows.PendingScanListAsync(db.OwnerConnectionString, Ct)).ShouldContain(poisonId);
                (await PlatformAuditsAsync("vendor.document_parked", poisonId)).ShouldBeEmpty();
            }

            await RunRescanJobAsync(scanner);
            (await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, poisonId, Ct)).Attempts.ShouldBe(run);
        }

        (await VendorDocumentRows.PendingScanListAsync(db.OwnerConnectionString, Ct)).ShouldNotContain(poisonId);
        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Single(r => r.Id == poisonId).ScanStatus.ShouldBe("pending_scan");
        // Parked: a further run no longer tries or counts it.
        await RunRescanJobAsync(scanner);
        (await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, poisonId, Ct)).Attempts.ShouldBe(12);
        var parked = (await PlatformAuditsAsync("vendor.document_parked", poisonId)).ShouldHaveSingleItem();
        parked.SubjectId.ShouldBe(companyId.ToString("D"));
        parked.ActorId.ShouldBeNull();

        // A platform operator looked at it and unparks it (docs/07 section 4), audited: the retry job tries it again from zero.
        (await VendorDocumentRows.UnparkAsync(db.OwnerConnectionString, poisonId, Ct)).ShouldBeTrue();
        var (attempts, lastScanAt) = await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, poisonId, Ct);
        attempts.ShouldBe(0);
        lastScanAt.ShouldBeNull();
        (await VendorDocumentRows.PendingScanListAsync(db.OwnerConnectionString, Ct)).ShouldContain(poisonId);
        var unparked = (await PlatformAuditsAsync("vendor.document_unparked", poisonId)).ShouldHaveSingleItem();
        unparked.ActorId.ShouldBe(new Npgsql.NpgsqlConnectionStringBuilder(db.OwnerConnectionString).Username);
        unparked.SubjectId.ShouldBe(companyId.ToString("D"));
        (await VendorDocumentRows.UnparkAsync(db.OwnerConnectionString, poisonId, Ct)).ShouldBeFalse();
        (await PlatformAuditsAsync("vendor.document_unparked", poisonId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_missing_quarantine_file_counts_as_a_scan_attempt()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var documentId = await PendingAsync(new Uploads(factory, vendor), VendorDocumentTypes.CrCertificate, VendorDocumentRows.Pdf(33_000));
        await using (var host = ServiceHost(clamAv.Settings))
        {
            await host.Services.GetRequiredService<IObjectStorage>().DeleteAsync($"vendors/{companyId}/quarantine/{documentId}", Ct);
        }

        await RunRescanJobAsync(new ScriptedScanner(_ => ScanResult.Clean));

        var (attempts, lastScanAt) = await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, documentId, Ct);
        attempts.ShouldBe(1);
        lastScanAt.ShouldNotBeNull();
        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().ScanStatus.ShouldBe("pending_scan");
    }

    [Fact]
    public async Task An_audit_failure_on_an_infected_upload_is_retried_and_audited()
    {
        var (vendor, companyId) = await VendorAsync();
        var failures = new AuditFailures(1);
        await using var host = ServiceHost(clamAv.Settings, services => FailAudits(services, failures));
        var uploadId = await StartAndSendAsync(host, companyId, vendor.Subject, VendorDocumentRows.InfectedPdf());

        // The audit is written before the outcome commits: when it fails, nothing is recorded and the upload stays open.
        await Should.ThrowAsync<InvalidOperationException>(() => CompleteThroughServiceAsync(host, companyId, vendor.Subject, uploadId, Ct));
        (await VendorDocumentRows.UploadOutcomeAsync(db.OwnerConnectionString, uploadId, Ct)).ShouldBeNull();
        (await PlatformAuditsForCompanyAsync(companyId)).ShouldBeEmpty();

        var again = await CompleteThroughServiceAsync(host, companyId, vendor.Subject, uploadId, Ct);

        again.Error.ShouldNotBeNull().Code.ShouldBe(VendorDocumentErrors.Infected);
        (await VendorDocumentRows.UploadOutcomeAsync(db.OwnerConnectionString, uploadId, Ct)).ShouldBe("infected");
        var audit = JsonDocument.Parse((await PlatformAuditsForCompanyAsync(companyId)).ShouldHaveSingleItem()).RootElement;
        audit.GetProperty("upload_id").GetString().ShouldBe(uploadId.ToString("D"));
    }

    [Fact]
    public async Task A_failed_audit_in_the_retry_scan_leaves_the_document_pending_and_the_next_run_audits_and_marks_it()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var file = VendorDocumentRows.Pdf(35_000);
        var documentId = await PendingAsync(new Uploads(factory, vendor), VendorDocumentTypes.CrCertificate, file);
        var quarantined = $"vendors/{companyId}/quarantine/{documentId}";
        var scanner = new ScriptedScanner(content => content.AsSpan().SequenceEqual(file) ? ScanResult.Infected("Eicar-Signature") : ScanResult.Clean);
        var failures = new AuditFailures(1);

        await RunRescanJobAsync(scanner, configure: services => FailAudits(services, failures));

        var row = (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();
        row.ScanStatus.ShouldBe("pending_scan");
        (await minio.ReadAsync(quarantined, Ct)).ShouldBe(file);
        (await PlatformAuditsForCompanyAsync(companyId)).ShouldBeEmpty();
        // A failure after the scan (the audit) is not the file's: the document moves back in the queue, nothing is charged.
        var (attempts, lastScanAt) = await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, documentId, Ct);
        attempts.ShouldBe(0);
        lastScanAt.ShouldNotBeNull();
        (await VendorDocumentRows.PendingScanListAsync(db.OwnerConnectionString, Ct)).ShouldContain(documentId);

        await RunRescanJobAsync(scanner);

        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().ScanStatus.ShouldBe("infected");
        (await minio.ReadAsync(quarantined, Ct)).ShouldBeNull();
        var audit = JsonDocument.Parse((await PlatformAuditsForCompanyAsync(companyId)).ShouldHaveSingleItem()).RootElement;
        audit.GetProperty("document_id").GetString().ShouldBe(documentId.ToString("D"));
    }

    [Fact]
    public async Task A_file_the_scanner_errors_on_is_charged_at_once_when_the_canary_scans_clean()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var uploads = new Uploads(factory, vendor);
        var files = Enumerable.Range(0, 6).Select(i => VendorDocumentRows.Pdf(36_000 + i)).ToList();
        var ids = new List<Guid>();
        foreach (var file in files)
        {
            ids.Add(await PendingAsync(uploads, VendorDocumentTypes.CrCertificate, file));
        }

        // Failed, Clean, Failed, Failed, Failed, Failed; the canary scans clean, so each error is the file's own.
        var clean = files[1];
        await RunRescanJobAsync(new ScriptedScanner(content =>
            files.Exists(f => content.AsSpan().SequenceEqual(f)) && !content.AsSpan().SequenceEqual(clean) ? ScanResult.Failed : ScanResult.Clean));

        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Single(r => r.Id == ids[1]).ScanStatus.ShouldBe("clean");
        var attempts = new List<int>();
        foreach (var id in ids)
        {
            attempts.Add((await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, id, Ct)).Attempts);
        }

        // No outage: the breaker never trips, so the run reaches and charges every failing file.
        attempts.ShouldBe([1, 0, 1, 1, 1, 1]);
    }

    [Fact]
    public async Task A_clamd_outage_where_the_canary_gets_no_verdict_either_charges_nothing_and_stops_the_run()
    {
        var (vendor, _) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var uploads = new Uploads(factory, vendor);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await PendingAsync(uploads, VendorDocumentTypes.CrCertificate, VendorDocumentRows.Pdf(39_000 + i)));
        }

        var scanner = new CountingScanner(ScanResult.Unavailable);

        await RunRescanJobAsync(scanner, runs: 12);

        // Each run: three documents, each followed by the canary, then the breaker stops it. (Documents of other tests
        // whose quarantined files are gone still count against themselves: a missing file is not an outage.)
        scanner.Scans.ShouldBe(12 * 3 * 2);
        foreach (var id in ids)
        {
            (await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, id, Ct)).Attempts.ShouldBe(0);
            (await VendorDocumentRows.PendingScanListAsync(db.OwnerConnectionString, Ct)).ShouldContain(id);
        }
    }

    [Fact]
    public async Task A_storage_outage_on_every_document_across_twelve_runs_charges_and_parks_nothing()
    {
        var (vendor, _) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var documentId = await PendingAsync(new Uploads(factory, vendor), VendorDocumentTypes.CrCertificate, VendorDocumentRows.Pdf(37_000));
        var before = await VendorDocumentRows.PendingAttemptsAsync(db.OwnerConnectionString, Ct);

        await RunRescanJobAsync(
            new ScriptedScanner(_ => ScanResult.Clean),
            runs: 12,
            configure: services => services.Replace(ServiceDescriptor.Singleton<IObjectStorage>(new UnreachableStorage())));

        var after = await VendorDocumentRows.PendingAttemptsAsync(db.OwnerConnectionString, Ct);
        after.ShouldBe(before);
        (await VendorDocumentRows.PendingScanListAsync(db.OwnerConnectionString, Ct)).ShouldContain(documentId);
    }

    [Fact]
    public async Task A_document_the_scanner_gives_no_verdict_for_does_not_hold_the_head_of_the_queue()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var uploads = new Uploads(factory, vendor);
        var stuck = VendorDocumentRows.Pdf(38_000);
        var stuckId = await PendingAsync(uploads, VendorDocumentTypes.CrCertificate, stuck);
        var laterId = await PendingAsync(uploads, VendorDocumentTypes.VatCertificate, VendorDocumentRows.Pdf(38_100));
        // A timeout or a limit reply on this one file: Unavailable, as an outage would be.
        var scanner = new ScriptedScanner(content => content.AsSpan().SequenceEqual(stuck) ? ScanResult.Unavailable : ScanResult.Clean);

        await RunRescanJobAsync(scanner);

        // The run went on past it, and the verdict on the next document charges it one attempt.
        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Single(r => r.Id == laterId).ScanStatus.ShouldBe("clean");
        var (attempts, lastScanAt) = await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, stuckId, Ct);
        lastScanAt.ShouldNotBeNull();
        attempts.ShouldBe(1);

        // Rotated: a document that arrives later is tried before it.
        var freshId = await PendingAsync(uploads, VendorDocumentTypes.CrCertificate, VendorDocumentRows.Pdf(38_200));
        var queue = (await VendorDocumentRows.PendingScanListAsync(db.OwnerConnectionString, Ct)).ToList();
        queue.IndexOf(freshId).ShouldBeLessThan(queue.IndexOf(stuckId));
    }

    [Fact]
    public async Task Ten_wrong_type_completions_do_not_block_an_eleventh_start()
    {
        var (vendor, _) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        var html = "<html><body>not a pdf</body></html>"u8.ToArray();
        var refused = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", html.Length, "application/pdf", expectedChunks: 1);
            await uploads.SendAllAsync(uploadId, html);
            using var complete = await uploads.CompleteAsync(uploadId, NextYear);
            complete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(complete)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.WrongType);
            refused.Add(uploadId);
        }

        foreach (var uploadId in refused)
        {
            (await VendorDocumentRows.UploadOutcomeAsync(db.OwnerConnectionString, uploadId, Ct)).ShouldBe("refused");
            (await minio.ReadAsync($"staging/{uploadId}/0", Ct)).ShouldBeNull();
        }

        // Completing a refused upload again answers the same refusal.
        using (var again = await uploads.CompleteAsync(refused[0], NextYear))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(again)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.WrongType);
        }

        await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf", expectedChunks: 1);
    }

    [Fact]
    public async Task An_expiry_refusal_leaves_the_upload_open_for_a_corrected_date()
    {
        var (vendor, _) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.Pdf(20_000);
        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 1);
        await uploads.SendAllAsync(uploadId, file);

        using (var wrongDate = await uploads.CompleteAsync(uploadId, new DateOnly(1999, 12, 31)))
        {
            wrongDate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await Json(wrongDate)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.InvalidExpiry);
        }

        (await VendorDocumentRows.UploadOutcomeAsync(db.OwnerConnectionString, uploadId, Ct)).ShouldBeNull();
        using var corrected = await uploads.CompleteAsync(uploadId, NextYear);
        corrected.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_upload_idle_for_more_than_an_hour_is_not_counted_as_open()
    {
        var (vendor, _) = await VendorAsync();
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        var open = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            open.Add(await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf", expectedChunks: 1));
        }

        // Both started two hours ago; the first had a chunk just now, so only the second is abandoned.
        using (var chunk = await uploads.PutChunkAsync(open[0], 0, System.Security.Cryptography.RandomNumberGenerator.GetBytes(1000)))
        {
            chunk.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await VendorDocumentRows.AgeUploadAsync(db.OwnerConnectionString, open[0], TimeSpan.FromHours(2), Ct);
        await VendorDocumentRows.AgeUploadAsync(db.OwnerConnectionString, open[1], TimeSpan.FromHours(2), Ct);

        await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf", expectedChunks: 1);
        using var twelfth = await uploads.StartRawAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf");
        twelfth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await Json(twelfth)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.TooManyUploads);
    }

    [Fact]
    public async Task The_daily_upload_bound_counts_every_start_whatever_its_outcome()
    {
        var (vendor, _) = await VendorAsync();
        await using var factory = Factory(settings: new Dictionary<string, string?> { ["Vendors:MaxUploadsPerDay"] = "4" });
        var uploads = new Uploads(factory, vendor);
        var html = "<html><body>not a pdf</body></html>"u8.ToArray();

        await uploads.UploadAsync(VendorDocumentTypes.CrCertificate, VendorDocumentRows.Pdf(20_000), NextYear);
        for (var i = 0; i < 2; i++)
        {
            var refusedId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", html.Length, "application/pdf", expectedChunks: 1);
            await uploads.SendAllAsync(refusedId, html);
            using var complete = await uploads.CompleteAsync(refusedId, NextYear);
            complete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var openId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf", expectedChunks: 1);

        using (var fifth = await uploads.StartRawAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf"))
        {
            fifth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (await Json(fifth)).GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.TooManyUploads);
        }

        // Rolling 24 hours: once a start is older than that, it no longer counts.
        await VendorDocumentRows.AgeUploadAsync(db.OwnerConnectionString, openId, TimeSpan.FromHours(25), Ct);
        await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", 1000, "application/pdf", expectedChunks: 1);
    }

    private async Task<IReadOnlyList<(string? ActorId, string? SubjectId)>> PlatformAuditsAsync(string action, Guid documentId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "select actor_id, subject_id from ops.platform_audit where action = @action and data ->> 'document_id' = @document", connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("document", documentId.ToString("D"));
        var result = new List<(string?, string?)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return result;
    }

    /// <summary>Uploads the file while the scanner is down and returns the pending document's id.</summary>
    private static async Task<Guid> PendingAsync(Uploads uploads, string type, byte[] file)
    {
        var uploadId = await uploads.StartAsync(type, "file.pdf", file.Length, "application/pdf", (file.Length + Megabyte - 1) / Megabyte);
        await uploads.SendAllAsync(uploadId, file);
        using var complete = await uploads.CompleteAsync(uploadId, NextYear);
        complete.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await Json(complete)).GetProperty("documentId").GetGuid();
    }

    /// <summary>
    /// The modules as the worker wires them, as its own role (W-36), with object storage and the given scanner settings,
    /// without HTTP.
    /// </summary>
    private ModuleHost ServiceHost(IReadOnlyDictionary<string, string?> scanner, Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(minio.Settings.Concat(scanner)).Build();
        return new ModuleHost(db.WorkerConnectionString, objectStorage: configuration, configure: services =>
        {
            services.AddVirusScanner(configuration);
            services.AddVendorJobs();
            configure?.Invoke(services);
        });
    }

    private ClamAvScanner RealScanner() =>
        new(ClamAvSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(clamAv.Settings).Build()), NullLogger<ClamAvScanner>.Instance);

    private static async Task<Guid> StartAndSendAsync(ModuleHost host, Guid companyId, string userId, byte[] file)
    {
        await using var scope = host.ScopeFor(null, companyId, userId);
        var uploads = scope.ServiceProvider.GetRequiredService<IVendorUploads>();
        var started = await uploads.StartAsync(new VendorUploadStart(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf"), Ct);
        started.IsSuccess.ShouldBeTrue();
        for (var index = 0; index < started.Value.ChunkCount; index++)
        {
            var chunk = Slice(file, index);
            (await uploads.PutChunkAsync(started.Value.UploadId, index, chunk, VendorDocumentRows.Sha256(chunk), Ct)).IsSuccess.ShouldBeTrue();
        }

        return started.Value.UploadId;
    }

    private static async Task<Result<VendorDocumentAdded>> CompleteThroughServiceAsync(
        ModuleHost host, Guid companyId, string userId, Guid uploadId, CancellationToken cancellationToken)
    {
        await using var scope = host.ScopeFor(null, companyId, userId);
        return await scope.ServiceProvider.GetRequiredService<IVendorUploads>().CompleteAsync(uploadId, NextYear, cancellationToken);
    }

    /// <summary>Fails the first statement that records an upload's outcome, after it ran, as a crash before the commit would.</summary>
    private sealed class FailFirstOutcomeUpdate : DbCommandInterceptor
    {
        private int _failed;

        public bool Failed => Volatile.Read(ref _failed) == 1;

        public override ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command, CommandExecutedEventData eventData, System.Data.Common.DbDataReader result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            System.Data.Common.DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void Fail(System.Data.Common.DbCommand command)
        {
            // EF's update of the row assigns the outcome a parameter; the chunk statement only reads it ("outcome is null").
            if (command.CommandText.Contains("UPDATE vendor.uploads", StringComparison.OrdinalIgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(command.CommandText, @"\boutcome\s*=\s*@", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
            {
                throw new InvalidOperationException("Simulated failure after the upload's outcome was written, before the commit.");
            }
        }
    }

    /// <summary>The real scanner, then the caller's request is aborted, as a browser that goes away mid-completion.</summary>
    private sealed class CancelAfterScan(IVirusScanner inner, CancellationTokenSource abort) : IVirusScanner
    {
        public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
        {
            var result = await inner.ScanAsync(content, cancellationToken);
            await abort.CancelAsync();
            return result;
        }
    }

    /// <summary>A scanner whose verdict the test decides from the content.</summary>
    private sealed class ScriptedScanner(Func<byte[], ScanResult> verdict) : IVirusScanner
    {
        public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            return verdict(buffer.ToArray());
        }
    }

    /// <summary>Holds every scan until the test releases it, so a second request overlaps the first for sure.</summary>
    private sealed class GatedScanner : IVirusScanner
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return ScanResult.Clean;
        }
    }

    /// <summary>Gives every scan the same result and counts the scans.</summary>
    private sealed class CountingScanner(ScanResult result) : IVirusScanner
    {
        private int _scans;

        public int Scans => Volatile.Read(ref _scans);

        public Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _scans);
            return Task.FromResult(result);
        }
    }

    /// <summary>Object storage that cannot be reached: every call throws, as during a MinIO outage.</summary>
    private sealed class UnreachableStorage : IObjectStorage
    {
        public Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default) => Fail();

        public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default) => Fail();

        public Task<StoredObject?> OpenAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromException<StoredObject?>(new HttpRequestException("Simulated object storage outage."));

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Fail();

        public Task<StoredObjectInfo?> GetInfoAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromException<StoredObjectInfo?>(new HttpRequestException("Simulated object storage outage."));

        public Task<IReadOnlyList<StoredObjectInfo>> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<StoredObjectInfo>>(new HttpRequestException("Simulated object storage outage."));

        private static Task Fail() => Task.FromException(new HttpRequestException("Simulated object storage outage."));
    }

    /// <summary>How many platform audit writes still fail, shared by every scope of a host.</summary>
    private sealed class AuditFailures(int count)
    {
        private int _left = count;

        public bool TryFail() => Interlocked.Decrement(ref _left) >= 0;
    }

    /// <summary>The real platform audit, except that the first writes fail as a database outage would.</summary>
    private sealed class FailingAudit(IPlatformAudit inner, AuditFailures failures) : IPlatformAudit
    {
        public Task WriteAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default) =>
            failures.TryFail()
                ? Task.FromException(new InvalidOperationException("Simulated platform audit failure."))
                : inner.WriteAsync(entry, cancellationToken);
    }

    private static void FailAudits(IServiceCollection services, AuditFailures failures)
    {
        services.AddScoped<PlatformAuditWriter>();
        services.Replace(ServiceDescriptor.Scoped<IPlatformAudit>(sp => new FailingAudit(sp.GetRequiredService<PlatformAuditWriter>(), failures)));
    }

    private static IReadOnlyDictionary<string, string?> ScannerDown => new Dictionary<string, string?>
    {
        ["ClamAv:Host"] = "127.0.0.1",
        ["ClamAv:Port"] = UnusedPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ClamAv:TimeoutSeconds"] = "5",
    };

    private WebApplicationFactory<Program> Factory(
        bool scannerUp = true, IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null) =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in minio.Settings.Concat(scannerUp ? clamAv.Settings : ScannerDown).Concat(settings ?? new Dictionary<string, string?>()))
            {
                builder.UseSetting(key, value);
            }

            if (services is not null)
            {
                builder.ConfigureTestServices(services);
            }
        });

    /// <summary>The worker's retry job, run once in a host wired as the worker wires it, with the given scanner.</summary>
    private async Task RunRescanJobAsync(IReadOnlyDictionary<string, string?> scanner)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(minio.Settings.Concat(scanner)).Build();
        await using var host = new ModuleHost(db.WorkerConnectionString, objectStorage: configuration, configure: services =>
        {
            services.AddVirusScanner(configuration);
            services.AddVendorJobs();
        });
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<VendorDocumentRescanJob>().RunAsync(Ct);
    }

    /// <summary>The worker's retry job with a scanner the test scripts, run <paramref name="runs"/> times in one worker host.</summary>
    private async Task RunRescanJobAsync(IVirusScanner scanner, int runs = 1, Action<IServiceCollection>? configure = null)
    {
        await using var host = ServiceHost(clamAv.Settings, services =>
        {
            services.Replace(ServiceDescriptor.Singleton(scanner));
            configure?.Invoke(services);
        });
        for (var run = 0; run < runs; run++)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<VendorDocumentRescanJob>().RunAsync(Ct);
        }
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

        public Task<HttpResponseMessage> PutChunkAsync(
            Guid uploadId, int index, byte[] content, string? sha256 = null, bool withHash = true, bool withAntiforgery = true)
        {
            var body = new ByteArrayContent(content);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return SendAsync(
                HttpMethod.Put, $"/vendor/uploads/{uploadId}/chunks/{index}", body,
                withHash ? sha256 ?? VendorDocumentRows.Sha256(content) : null, withAntiforgery);
        }

        public async Task SendAllAsync(Guid uploadId, byte[] file)
        {
            for (var index = 0; index * Megabyte < file.Length; index++)
            {
                using var response = await PutChunkAsync(uploadId, index, Slice(file, index));
                response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }
        }

        public Task<HttpResponseMessage> CompleteAsync(Guid uploadId, DateOnly expiresOn, bool withAntiforgery = true) =>
            SendAsync(HttpMethod.Post, $"/vendor/uploads/{uploadId}/complete", JsonContent.Create(new { expiresOn }), withAntiforgery: withAntiforgery);

        /// <summary>Start, every chunk, complete; the file must scan clean. Returns the document id.</summary>
        public async Task<Guid> UploadAsync(string documentType, byte[] file, DateOnly expiresOn)
        {
            var uploadId = await StartAsync(documentType, "file.pdf", file.Length, "application/pdf", (file.Length + Megabyte - 1) / Megabyte);
            await SendAllAsync(uploadId, file);
            using var complete = await CompleteAsync(uploadId, expiresOn);
            complete.StatusCode.ShouldBe(HttpStatusCode.OK);
            return (await Json(complete)).GetProperty("documentId").GetGuid();
        }

        private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent content, string? sha256 = null, bool withAntiforgery = true)
        {
            var request = new HttpRequestMessage(method, path) { Content = content }.As(user);
            if (withAntiforgery)
            {
                request.Headers.Add("RequestVerificationToken", _tokens.RequestToken);
                request.Headers.Add("Cookie", $"{_tokens.CookieName}={_tokens.CookieToken}");
            }

            if (sha256 is not null)
            {
                request.Headers.Add("X-Chunk-Sha256", sha256);
            }

            return _client.SendAsync(request, Ct);
        }
    }
}
