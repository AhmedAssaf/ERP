using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Tenancy.Contracts;

namespace Platform.Web.PlatformHost;

/// <summary>One line of the console's tenant table (F-54 as narrowed). Null counts are unknown and shown as a dash.</summary>
internal sealed record TenantRow(TenantSummary Tenant, int? Users, long? StorageBytes, int FailedJobs);

/// <summary>A failed job with the portal name of the tenant it carries; no tenant means a platform job.</summary>
internal sealed record FailedJobRow(FailedJob Job, string? TenantName);

internal sealed record TenantsAndJobs(IReadOnlyList<TenantRow> Tenants, IReadOnlyList<FailedJobRow> FailedJobs);

/// <summary>
/// Gathers the tenant list for <c>/platform/tenants</c> from the modules' public contracts: tenants (Tenancy), user
/// counts (Identity), storage and failed jobs (Operations). Used only by console pages, after PlatformAdmin passed.
/// The per-tenant lookups go to Keycloak and object storage, so they run side by side, at most
/// <see cref="MaxParallel"/> tenants at a time; both services cache their answers (60 s and 10 min).
/// </summary>
internal sealed class TenantOverview(
    ITenantCatalog catalog, IOrganizationMembers members, ITenantStorageUsage storage, IPlatformJobs jobs)
{
    internal const int MaxParallel = 4;

    public async Task<TenantsAndJobs> LoadAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await catalog.ListAsync(cancellationToken);
        var failed = await jobs.FailedAsync(cancellationToken: cancellationToken);

        using var slots = new SemaphoreSlim(MaxParallel);
        var rows = await Task.WhenAll(tenants.Select(async tenant =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                var users = members.CountAsync(tenant.OrganizationAlias, cancellationToken);
                var bytes = storage.UsedBytesAsync(tenant.Id, cancellationToken);
                return new TenantRow(tenant, await users, await bytes, failed.Count(j => j.TenantId == tenant.Id));
            }
            finally
            {
                slots.Release();
            }
        }));

        var names = tenants.ToDictionary(t => t.Id, t => t.PortalName);
        var failedRows = failed
            .Select(j => new FailedJobRow(j, j.TenantId is { } id && names.TryGetValue(id, out var name) ? name : null))
            .ToList();
        return new TenantsAndJobs(rows, failedRows);
    }
}
