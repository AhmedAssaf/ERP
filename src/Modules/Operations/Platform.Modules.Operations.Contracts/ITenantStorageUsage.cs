namespace Platform.Modules.Operations.Contracts;

/// <summary>
/// Object storage used by a tenant: the sum of object sizes under <c>tenants/{tenantId}/</c>, cached for ten minutes
/// (D-12). Null when object storage is not configured or did not answer; the console then shows a dash.
/// </summary>
public interface ITenantStorageUsage
{
    Task<long?> UsedBytesAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
