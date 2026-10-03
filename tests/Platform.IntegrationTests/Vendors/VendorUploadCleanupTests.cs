using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Documents;
using Platform.Shared;
using Platform.Shared.Jobs;
using Platform.Shared.Storage;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// The worker's vendor document jobs (vendor plan task 3): uploads abandoned for more than a day are deleted with their
/// staged chunks, younger ones are left alone, and the application role can remove upload rows only through the cleanup
/// function, never directly. The retry scan runs every five minutes and the cleanup every hour.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorUploadCleanupTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Abandoned_uploads_past_the_25_hour_margin_are_deleted_with_their_chunks()
    {
        var companyId = await VendorRows.RegisterAsync(
            db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), VendorRows.NewCrNumber(), "Cleanup Company", Ct);
        await using var host = Host();
        var abandoned = await StartWithChunkAsync(host, companyId);
        var recent = await StartWithChunkAsync(host, companyId);
        await VendorDocumentRows.AgeUploadAsync(db.OwnerConnectionString, abandoned, TimeSpan.FromHours(26), Ct);
        // Past its usable day but inside the margin: a completion that began just before the day ended may still commit.
        await VendorDocumentRows.AgeUploadAsync(db.OwnerConnectionString, recent, TimeSpan.FromHours(24.5), Ct);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<VendorUploadCleanupJob>().RunAsync(Ct);
        }

        (await VendorDocumentRows.UploadExistsAsync(db.OwnerConnectionString, abandoned, Ct)).ShouldBeFalse();
        (await minio.ReadAsync($"staging/{abandoned}/0", Ct)).ShouldBeNull();
        (await VendorDocumentRows.UploadExistsAsync(db.OwnerConnectionString, recent, Ct)).ShouldBeTrue();
        (await minio.ReadAsync($"staging/{recent}/0", Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_stale_open_upload_takes_the_files_a_failed_completion_left_under_its_id()
    {
        var companyId = await VendorRows.RegisterAsync(
            db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), VendorRows.NewCrNumber(), "Orphan Company", Ct);
        await using var host = Host();
        var orphaned = await StartWithChunkAsync(host, companyId);
        var completed = await CompletePendingAsync(host, companyId);
        var storage = host.Services.GetRequiredService<IObjectStorage>();
        // A completion stored the file under the upload's id (clean or in quarantine), then failed before its commit.
        var file = VendorDocumentRows.Pdf(3000);
        await storage.PutAsync($"vendors/{companyId}/documents/{orphaned}", file, "application/pdf", Ct);
        await storage.PutAsync($"vendors/{companyId}/quarantine/{orphaned}", file, "application/pdf", Ct);
        await VendorDocumentRows.AgeUploadAsync(db.OwnerConnectionString, orphaned, TimeSpan.FromHours(26), Ct);
        await VendorDocumentRows.AgeUploadAsync(db.OwnerConnectionString, completed, TimeSpan.FromHours(26), Ct);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<VendorUploadCleanupJob>().RunAsync(Ct);
        }

        (await minio.ReadAsync($"vendors/{companyId}/documents/{orphaned}", Ct)).ShouldBeNull();
        (await minio.ReadAsync($"vendors/{companyId}/quarantine/{orphaned}", Ct)).ShouldBeNull();
        (await VendorDocumentRows.UploadExistsAsync(db.OwnerConnectionString, orphaned, Ct)).ShouldBeFalse();
        // A completed upload's document is the vendor's: only the upload row goes.
        (await minio.ReadAsync($"vendors/{companyId}/quarantine/{completed}", Ct)).ShouldNotBeNull();
        (await VendorDocumentRows.UploadExistsAsync(db.OwnerConnectionString, completed, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_cleanup_skips_an_upload_whose_row_a_completion_holds_and_takes_it_on_the_next_run()
    {
        var companyId = await VendorRows.RegisterAsync(
            db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), VendorRows.NewCrNumber(), "Edge Company", Ct);
        await using var host = Host();
        var uploadId = await StartWithChunkAsync(host, companyId);
        var documentKey = $"vendors/{companyId}/documents/{uploadId}";
        await host.Services.GetRequiredService<IObjectStorage>().PutAsync(documentKey, VendorDocumentRows.Pdf(3000), "application/pdf", Ct);
        await VendorDocumentRows.AgeUploadAsync(db.OwnerConnectionString, uploadId, TimeSpan.FromHours(26), Ct);

        // A completion holds the upload's row lock (FOR UPDATE, as VendorUploads.CompleteAsync takes it) while it scans
        // and stores the file under the upload's id; the cleanup must not delete that file under it.
        await using (var completion = new NpgsqlConnection(db.AppConnectionString))
        {
            await completion.OpenAsync(Ct);
            await using var transaction = await completion.BeginTransactionAsync(Ct);
            await using (var hold = new NpgsqlCommand(
                "select set_config('app.vendor_company_id', @company, true); select 1 from vendor.uploads where id = @id for update",
                completion, transaction))
            {
                hold.Parameters.AddWithValue("company", companyId.ToString());
                hold.Parameters.AddWithValue("id", uploadId);
                await hold.ExecuteNonQueryAsync(Ct);
            }

            await RunCleanupAsync(host);

            (await minio.ReadAsync(documentKey, Ct)).ShouldNotBeNull();
            (await minio.ReadAsync($"staging/{uploadId}/0", Ct)).ShouldNotBeNull();
            (await VendorDocumentRows.UploadExistsAsync(db.OwnerConnectionString, uploadId, Ct)).ShouldBeTrue();
            await transaction.RollbackAsync(Ct);
        }

        await RunCleanupAsync(host);

        (await minio.ReadAsync(documentKey, Ct)).ShouldBeNull();
        (await VendorDocumentRows.UploadExistsAsync(db.OwnerConnectionString, uploadId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_application_role_cannot_delete_upload_rows_or_remove_a_young_upload()
    {
        var companyId = await VendorRows.RegisterAsync(
            db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), VendorRows.NewCrNumber(), "Delete Probe", Ct);
        await using var host = Host();
        var uploadId = await StartWithChunkAsync(host, companyId);

        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using (var setContext = new NpgsqlCommand("select set_config('app.vendor_company_id', @company, false)", connection))
        {
            setContext.Parameters.AddWithValue("company", companyId.ToString());
            await setContext.ExecuteNonQueryAsync(Ct);
        }

        await using (var delete = new NpgsqlCommand("delete from vendor.uploads where id = @id", connection))
        {
            delete.Parameters.AddWithValue("id", uploadId);
            (await Should.ThrowAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(Ct))).SqlState.ShouldBe("42501");
        }

        // A web session (here a vendor's) may not call the worker's removal at all (ADR-0012, pentest P-3).
        await using (var remove = new NpgsqlCommand("select vendor.remove_stale_upload(@id)", connection))
        {
            remove.Parameters.AddWithValue("id", uploadId);
            (await Should.ThrowAsync<PostgresException>(() => remove.ExecuteScalarAsync(Ct))).SqlState.ShouldBe("42501");
        }

        // The worker's own session (no context) still cannot remove an upload younger than the margin.
        await using var worker = new NpgsqlConnection(db.WorkerConnectionString);
        await worker.OpenAsync(Ct);
        await using var young = new NpgsqlCommand("select vendor.remove_stale_upload(@id)", worker);
        young.Parameters.AddWithValue("id", uploadId);
        ((bool)(await young.ExecuteScalarAsync(Ct))!).ShouldBeFalse();
        (await VendorDocumentRows.UploadExistsAsync(db.OwnerConnectionString, uploadId, Ct)).ShouldBeTrue();
    }

    [Fact]
    public void The_worker_schedules_the_retry_scan_every_five_minutes_and_the_cleanup_every_hour()
    {
        // Storage only, no job server: nothing here runs the jobs, and the schedule is removed again afterwards so no
        // other test's job server picks it up from the shared database.
        var services = new ServiceCollection();
        services.AddJobClient(db.WorkerConnectionString);
        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<JobStorage>();
        try
        {
            VendorsModule.ScheduleVendorJobs(provider);

            using var connection = storage.GetConnection();
            var jobs = connection.GetRecurringJobs().ToDictionary(j => j.Id, j => j.Cron);
            jobs[VendorsModule.DocumentRescanJobId].ShouldBe("*/5 * * * *");
            jobs[VendorsModule.UploadCleanupJobId].ShouldBe(Cron.Hourly());
        }
        finally
        {
            var manager = new RecurringJobManager(storage);
            manager.RemoveIfExists(VendorsModule.DocumentRescanJobId);
            manager.RemoveIfExists(VendorsModule.UploadCleanupJobId);
        }
    }

    private static async Task RunCleanupAsync(ModuleHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<VendorUploadCleanupJob>().RunAsync(Ct);
    }

    // The worker's role (W-36): the cleanup is the worker's; the role also holds every application right, so the same host
    // starts the vendor's uploads.
    private ModuleHost Host()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(minio.Settings).Build();
        return new ModuleHost(db.WorkerConnectionString, objectStorage: configuration, configure: services =>
        {
            services.AddVirusScanner(configuration);
            services.AddVendorJobs();
        });
    }

    /// <summary>A vendor starts an upload and sends its first chunk, through the module's service.</summary>
    private static async Task<Guid> StartWithChunkAsync(ModuleHost host, Guid companyId)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId);
        var uploads = scope.ServiceProvider.GetRequiredService<IVendorUploads>();
        var chunk = VendorDocumentRows.Pdf(2000);
        var started = await uploads.StartAsync(
            new VendorUploadStart(VendorDocumentTypes.CrCertificate, "cr.pdf", (1024 * 1024) + 10, "application/pdf"), Ct);
        started.IsSuccess.ShouldBeTrue();
        var first = new byte[1024 * 1024];
        chunk.CopyTo(first, 0);
        (await uploads.PutChunkAsync(started.Value.UploadId, 0, first, VendorDocumentRows.Sha256(first), Ct)).IsSuccess.ShouldBeTrue();
        return started.Value.UploadId;
    }

    /// <summary>A whole upload completed while no scanner is configured: its document waits in quarantine under the upload's id.</summary>
    private static async Task<Guid> CompletePendingAsync(ModuleHost host, Guid companyId)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId);
        var uploads = scope.ServiceProvider.GetRequiredService<IVendorUploads>();
        var file = VendorDocumentRows.Pdf(4000);
        var started = await uploads.StartAsync(
            new VendorUploadStart(VendorDocumentTypes.CrCertificate, "cr.pdf", file.Length, "application/pdf"), Ct);
        started.IsSuccess.ShouldBeTrue();
        (await uploads.PutChunkAsync(started.Value.UploadId, 0, file, VendorDocumentRows.Sha256(file), Ct)).IsSuccess.ShouldBeTrue();
        var completed = await uploads.CompleteAsync(started.Value.UploadId, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1), Ct);
        completed.IsSuccess.ShouldBeTrue();
        completed.Value.DocumentId.ShouldBe(started.Value.UploadId);
        return started.Value.UploadId;
    }
}
