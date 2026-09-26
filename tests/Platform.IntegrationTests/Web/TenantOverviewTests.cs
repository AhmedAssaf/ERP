using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Web.PlatformHost;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// The console's tenant list asks Keycloak and object storage once per tenant; those calls run side by side, at most
/// <see cref="TenantOverview.MaxParallel"/> at a time, and the rows keep the catalog's order (review of plan task 7).
/// </summary>
public sealed class TenantOverviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Per_tenant_lookups_run_in_parallel_within_the_bound_and_keep_the_catalog_order()
    {
        var tenants = Enumerable.Range(1, 12)
            .Select(i => new TenantSummary(Guid.NewGuid(), $"t{i:00}", $"org{i:00}", $"Tenant {i:00}", "active"))
            .ToList();
        var keycloak = new ConcurrencyProbe();
        var storage = new ConcurrencyProbe();
        var overview = new TenantOverview(
            new FixedCatalog(tenants), new SlowMembers(keycloak), new SlowStorage(storage), new NoFailedJobs());

        var result = await overview.LoadAsync(Ct);

        result.Tenants.Select(r => r.Tenant.Slug).ShouldBe(tenants.Select(t => t.Slug));
        foreach (var (row, index) in result.Tenants.Select((r, i) => (r, i)))
        {
            row.Users.ShouldBe(index + 1);
        }

        result.Tenants.ShouldAllBe(r => r.StorageBytes == 100);
        foreach (var probe in new[] { keycloak, storage })
        {
            probe.Max.ShouldBeGreaterThan(1, "the lookups ran one after another");
            probe.Max.ShouldBeLessThanOrEqualTo(TenantOverview.MaxParallel);
        }
    }

    private sealed class ConcurrencyProbe
    {
        private int _current;
        private int _max;

        public int Max => Volatile.Read(ref _max);

        public async Task<T> RunAsync<T>(T value, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(40, cancellationToken);
                return value;
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private sealed class FixedCatalog(IReadOnlyList<TenantSummary> tenants) : ITenantCatalog
    {
        public Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(tenants);
    }

    private sealed class SlowMembers(ConcurrencyProbe probe) : IOrganizationMembers
    {
        public Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default) =>
            probe.RunAsync<int?>(int.Parse(organizationAlias.AsSpan(3), System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    private sealed class SlowStorage(ConcurrencyProbe probe) : ITenantStorageUsage
    {
        public Task<long?> UsedBytesAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            probe.RunAsync<long?>(100, cancellationToken);
    }

    private sealed class NoFailedJobs : IPlatformJobs
    {
        public Task<IReadOnlyList<FailedJob>> FailedAsync(int limit = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FailedJob>>([]);

        public Task<RequeueOutcome> RequeueAsync(string jobId, string actorId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
