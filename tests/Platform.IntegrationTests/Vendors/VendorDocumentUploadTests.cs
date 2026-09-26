using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
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
        await using var factory = Factory();
        var uploads = new Uploads(factory, vendor);
        var file = VendorDocumentRows.Pdf((3 * Megabyte) + 777);
        var uploadId = await uploads.StartAsync(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf", expectedChunks: 4);
        await uploads.SendAllAsync(uploadId, file);

        var responses = await Task.WhenAll(uploads.CompleteAsync(uploadId, NextYear), uploads.CompleteAsync(uploadId, NextYear));

        var documentIds = new List<Guid>();
        foreach (var response in responses)
        {
            using (response)
            {
                response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
                var body = await Json(response);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    body.GetProperty("code").GetString().ShouldBe(VendorDocumentErrors.UploadInProgress);
                }
                else
                {
                    documentIds.Add(body.GetProperty("documentId").GetGuid());
                }
            }
        }

        documentIds.ShouldNotBeEmpty();
        documentIds.Distinct().ShouldHaveSingleItem();
        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().Id.ShouldBe(documentIds[0]);
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
            audits.ShouldHaveSingleItem();
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
    public async Task After_twelve_failed_attempts_a_pending_document_is_parked_for_a_person()
    {
        var (vendor, companyId) = await VendorAsync();
        await using var factory = Factory(scannerUp: false);
        var bad = VendorDocumentRows.Pdf(32_000);
        var badId = await PendingAsync(new Uploads(factory, vendor), VendorDocumentTypes.CrCertificate, bad);
        var scanner = new ScriptedScanner(content => content.AsSpan().SequenceEqual(bad) ? ScanResult.Failed : ScanResult.Clean);

        await RunRescanJobAsync(scanner, runs: 11);
        (await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, badId, Ct)).Attempts.ShouldBe(11);
        (await VendorDocumentRows.PendingScanListAsync(db.OwnerConnectionString, Ct)).ShouldContain(badId);

        await RunRescanJobAsync(scanner);

        (await VendorDocumentRows.ScanAttemptsAsync(db.OwnerConnectionString, badId, Ct)).Attempts.ShouldBe(12);
        (await VendorDocumentRows.PendingScanListAsync(db.OwnerConnectionString, Ct)).ShouldNotContain(badId);
        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().ScanStatus.ShouldBe("pending_scan");
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

    /// <summary>Uploads the file while the scanner is down and returns the pending document's id.</summary>
    private static async Task<Guid> PendingAsync(Uploads uploads, string type, byte[] file)
    {
        var uploadId = await uploads.StartAsync(type, "file.pdf", file.Length, "application/pdf", (file.Length + Megabyte - 1) / Megabyte);
        await uploads.SendAllAsync(uploadId, file);
        using var complete = await uploads.CompleteAsync(uploadId, NextYear);
        complete.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await Json(complete)).GetProperty("documentId").GetGuid();
    }

    /// <summary>The modules as the worker wires them, with object storage and the given scanner settings, without HTTP.</summary>
    private ModuleHost ServiceHost(IReadOnlyDictionary<string, string?> scanner, Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(minio.Settings.Concat(scanner)).Build();
        return new ModuleHost(db.AppConnectionString, objectStorage: configuration, configure: services =>
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

    private static IReadOnlyDictionary<string, string?> ScannerDown => new Dictionary<string, string?>
    {
        ["ClamAv:Host"] = "127.0.0.1",
        ["ClamAv:Port"] = UnusedPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ClamAv:TimeoutSeconds"] = "5",
    };

    private WebApplicationFactory<Program> Factory(bool scannerUp = true, IReadOnlyDictionary<string, string?>? settings = null) =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in minio.Settings.Concat(scannerUp ? clamAv.Settings : ScannerDown).Concat(settings ?? new Dictionary<string, string?>()))
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

    /// <summary>The worker's retry job with a scanner the test scripts, run <paramref name="runs"/> times in one worker host.</summary>
    private async Task RunRescanJobAsync(IVirusScanner scanner, int runs = 1)
    {
        await using var host = ServiceHost(clamAv.Settings, services => services.Replace(ServiceDescriptor.Singleton(scanner)));
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
