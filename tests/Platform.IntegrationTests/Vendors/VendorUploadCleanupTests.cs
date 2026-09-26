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
    public async Task Abandoned_uploads_older_than_a_day_are_deleted_with_their_chunks()
    {
        var companyId = await VendorRows.RegisterAsync(
            db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), VendorRows.NewCrNumber(), "Cleanup Company", Ct);
        await using var host = Host();
        var abandoned = await StartWithChunkAsync(host, companyId);
        var recent = await StartWithChunkAsync(host, companyId);
        await VendorDocumentRows.AgeUploadAsync(db.OwnerConnectionString, abandoned, TimeSpan.FromHours(25), Ct);

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

        await using var remove = new NpgsqlCommand("select vendor.remove_stale_upload(@id)", connection);
        remove.Parameters.AddWithValue("id", uploadId);
        ((bool)(await remove.ExecuteScalarAsync(Ct))!).ShouldBeFalse();
        (await VendorDocumentRows.UploadExistsAsync(db.OwnerConnectionString, uploadId, Ct)).ShouldBeTrue();
    }

    [Fact]
    public void The_worker_schedules_the_retry_scan_every_five_minutes_and_the_cleanup_every_hour()
    {
        // Storage only, no job server: nothing here runs the jobs, and the schedule is removed again afterwards so no
        // other test's job server picks it up from the shared database.
        var services = new ServiceCollection();
        services.AddJobClient(db.AppConnectionString);
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

    private ModuleHost Host()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(minio.Settings).Build();
        return new ModuleHost(db.AppConnectionString, objectStorage: configuration, configure: services =>
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
}
