using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Tenancy;
using Platform.Modules.Tenancy.Branding;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Jobs;
using Platform.Shared.Storage;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Tenancy;

/// <summary>
/// W-38 (F-02): a logo is written to object storage before the branding row is saved, so a save refused or failed after
/// the upload leaves an unreferenced object. <see cref="BrandingLogoCleanupJob"/> deletes the objects no tenant references
/// once they are older than the grace period; the save itself deletes nothing. The "later" the job sees is a clock two
/// hours ahead of the real one (the objects' write times are the storage's own).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BrandingLogoCleanupTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>
{
    private const string Actor = "logo-cleanup-admin";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string FakeKey(Guid tenantId, char digit) => $"tenants/{tenantId:D}/branding/logo-{new string(digit, 64)}.png";

    [Fact]
    public async Task The_job_deletes_an_old_unreferenced_logo_and_keeps_every_referenced_one_and_every_other_object()
    {
        var tenantA = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var tenantB = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var host = Host(DateTimeOffset.UtcNow.AddHours(2));

        // The same logo, so the same content hash, saved by two tenants: both keys are referenced.
        await SaveLogoAsync(host, tenantA, 200, 100);
        await SaveLogoAsync(host, tenantB, 200, 100);
        var referencedA = (await minio.ListKeysAsync($"tenants/{tenantA.TenantId:D}/branding/", Ct)).ShouldHaveSingleItem();
        var referencedB = (await minio.ListKeysAsync($"tenants/{tenantB.TenantId:D}/branding/", Ct)).ShouldHaveSingleItem();
        // Tenant A's second logo replaced the first in its row; the first stays in storage, now unreferenced.
        await SaveLogoAsync(host, tenantA, 300, 100);
        var keysA = await minio.ListKeysAsync($"tenants/{tenantA.TenantId:D}/branding/", Ct);
        keysA.Count.ShouldBe(2);
        var replacedA = referencedA;
        var currentA = keysA.Single(k => k != referencedA);

        var storage = host.Services.GetRequiredService<IObjectStorage>();
        var orphan = FakeKey(tenantB.TenantId, 'a');
        await storage.PutAsync(orphan, new byte[] { 1, 2, 3 }, "image/png", Ct);
        var notALogo = $"tenants/{tenantB.TenantId:D}/branding/notes.txt";
        await storage.PutAsync(notALogo, new byte[] { 4 }, "text/plain", Ct);

        await RunJobAsync(host);

        (await minio.ReadAsync(orphan, Ct)).ShouldBeNull();
        (await minio.ReadAsync(replacedA, Ct)).ShouldBeNull();
        (await minio.ReadAsync(currentA, Ct)).ShouldNotBeNull();
        (await minio.ReadAsync(referencedB, Ct)).ShouldNotBeNull();
        (await minio.ReadAsync(notALogo, Ct)).ShouldNotBeNull();
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenantA, Ct)).LogoUrl.ShouldNotBeNull();

        // Idempotent: a second run changes nothing and does not fail.
        await RunJobAsync(host);
        (await minio.ReadAsync(currentA, Ct)).ShouldNotBeNull();
        (await minio.ReadAsync(referencedB, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task The_job_keeps_an_unreferenced_logo_younger_than_the_grace_period()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var host = Host(DateTimeOffset.UtcNow);
        var young = FakeKey(tenant.TenantId, 'b');
        await host.Services.GetRequiredService<IObjectStorage>().PutAsync(young, new byte[] { 1 }, "image/png", Ct);

        await RunJobAsync(host);

        (await minio.ReadAsync(young, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_refused_save_leaves_its_object_for_the_job_and_the_job_removes_it_after_the_grace_period()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        const string stranger = "logo-cleanup-stranger";
        await using var host = Host(DateTimeOffset.UtcNow.AddHours(2));
        await using (var scope = host.ScopeFor(tenant, actingUserId: stranger))
        {
            await using var upload = new MemoryStream(BrandingServiceTests.Jpeg(200, 100));
            (await scope.ServiceProvider.GetRequiredService<IBrandingService>().SaveLogoAsync(upload, "image/jpeg", stranger, Ct))
                .Error.ShouldNotBeNull().Code.ShouldBe(BrandingErrors.NotAllowed);
        }

        var prefix = $"tenants/{tenant.TenantId:D}/branding/";
        (await minio.ListKeysAsync(prefix, Ct)).Count.ShouldBe(1);

        await RunJobAsync(host);

        (await minio.ListKeysAsync(prefix, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failure_after_the_commit_leaves_the_referenced_logo_in_storage_and_the_job_keeps_it()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, tenant.TenantId, Actor, Ct);
        await using var host = Host(DateTimeOffset.UtcNow.AddHours(2), failAudit: true);
        await using (var scope = host.ScopeFor(tenant, actingUserId: Actor))
        {
            await using var upload = new MemoryStream(BrandingServiceTests.Jpeg(200, 100));
            // The row is committed; the audit write after it fails, as a connection drop after the commit would.
            await Should.ThrowAsync<InvalidOperationException>(
                () => scope.ServiceProvider.GetRequiredService<IBrandingService>().SaveLogoAsync(upload, "image/jpeg", Actor, Ct));
        }

        var key = (await minio.ListKeysAsync($"tenants/{tenant.TenantId:D}/branding/", Ct)).ShouldHaveSingleItem();
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldNotBeNull();

        await RunJobAsync(host);

        (await minio.ReadAsync(key, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task An_old_unreferenced_object_that_is_refreshed_after_the_listing_is_kept()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var now = DateTimeOffset.UtcNow;
        var storage = new MemoryStorage();
        var key = FakeKey(tenant.TenantId, 'c');
        storage.Put(key, now.AddHours(-3));
        // A second save of the same logo puts the object again after the job listed it, and commits later.
        storage.AfterList = () => storage.Put(key, now);
        await using var host = Host(now, storage: storage);

        await RunJobAsync(host);

        storage.Has(key).ShouldBeTrue();
    }

    [Fact]
    public async Task An_old_unreferenced_object_that_gets_a_reference_after_the_listing_is_kept()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var hash = new string('d', 64);
        var key = FakeKey(tenant.TenantId, 'd');
        var storage = new MemoryStorage();
        storage.Put(key, DateTimeOffset.UtcNow.AddHours(-3));
        storage.AfterList = () => SetLogoUrlAsync(tenant.TenantId, $"/branding/logo/{hash}.png").GetAwaiter().GetResult();
        await using var host = Host(DateTimeOffset.UtcNow, storage: storage);

        await RunJobAsync(host);

        storage.Has(key).ShouldBeTrue();
    }

    [Fact]
    public async Task A_listing_error_for_one_tenant_does_not_end_the_run()
    {
        var failing = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var other = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var storage = new MemoryStorage { FailListFor = $"tenants/{failing.TenantId:D}/branding/" };
        var orphan = FakeKey(other.TenantId, 'e');
        storage.Put(orphan, DateTimeOffset.UtcNow.AddHours(-3));
        await using var host = Host(DateTimeOffset.UtcNow, storage: storage);

        await RunJobAsync(host);

        storage.Has(orphan).ShouldBeFalse();
    }

    [Fact]
    public void The_worker_schedules_the_logo_cleanup_every_hour()
    {
        var services = new ServiceCollection();
        services.AddJobClient(db.AppConnectionString);
        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<JobStorage>();
        try
        {
            TenancyModule.ScheduleBrandingJobs(provider);

            using var connection = storage.GetConnection();
            connection.GetRecurringJobs().ToDictionary(j => j.Id, j => j.Cron)[TenancyModule.LogoCleanupJobId].ShouldBe(Cron.Hourly());
        }
        finally
        {
            new RecurringJobManager(storage).RemoveIfExists(TenancyModule.LogoCleanupJobId);
        }
    }

    private async Task SaveLogoAsync(ModuleHost host, TenantContext tenant, int width, int height)
    {
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, tenant.TenantId, Actor, Ct);
        await using var scope = host.ScopeFor(tenant, actingUserId: Actor);
        await using var upload = new MemoryStream(BrandingServiceTests.Jpeg(width, height));
        (await scope.ServiceProvider.GetRequiredService<IBrandingService>().SaveLogoAsync(upload, "image/jpeg", Actor, Ct)).IsSuccess.ShouldBeTrue();
    }

    private static async Task RunJobAsync(ModuleHost host)
    {
        // A job's scope has neither a tenant nor a vendor context.
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BrandingLogoCleanupJob>().RunAsync(Ct);
    }

    private async Task SetLogoUrlAsync(Guid tenantId, string url)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("update tenancy.tenants set logo_url = @url where id = @id", connection);
        command.Parameters.AddWithValue("url", url);
        command.Parameters.AddWithValue("id", tenantId);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private ModuleHost Host(DateTimeOffset now, bool failAudit = false, IObjectStorage? storage = null) => new(
        db.AppConnectionString,
        clock: new FixedClock(now),
        objectStorage: new ConfigurationBuilder().AddInMemoryCollection(minio.Settings).Build(),
        configure: services =>
        {
            services.AddBrandingJobs(new ConfigurationBuilder().Build());
            if (storage is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(storage));
            }

            if (failAudit)
            {
                services.Replace(ServiceDescriptor.Scoped<IAuditWriter>(_ => new FailingAudit()));
            }
        });

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Object storage in memory with write times the test sets, and hooks for what happens between the job's steps.</summary>
    private sealed class MemoryStorage : IObjectStorage
    {
        private readonly Dictionary<string, DateTimeOffset> _objects = [];

        public Action? AfterList { get; set; }

        public string? FailListFor { get; init; }

        public void Put(string key, DateTimeOffset modified) => _objects[key] = modified;

        public bool Has(string key) => _objects.ContainsKey(key);

        public Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default)
        {
            Put(key, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }

        public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default) =>
            PutAsync(key, ReadOnlyMemory<byte>.Empty, contentType, cancellationToken);

        public Task<StoredObject?> OpenAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredObject?>(null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<StoredObjectInfo?> GetInfoAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_objects.TryGetValue(key, out var modified) ? new StoredObjectInfo(key, modified) : null);

        public Task<IReadOnlyList<StoredObjectInfo>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        {
            if (prefix == FailListFor)
            {
                throw new HttpRequestException("Simulated listing failure.");
            }

            IReadOnlyList<StoredObjectInfo> found = [.. _objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(o => new StoredObjectInfo(o.Key, o.Value))];
            AfterList?.Invoke();
            return Task.FromResult(found);
        }
    }

    private sealed class FailingAudit : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Simulated failure after the commit."));
    }
}
