using Platform.Shared.Tenancy;

namespace Platform.Modules.Tenancy.Contracts;

public interface ITenantDirectory
{
    /// <summary>
    /// The tenant that owns the host name, or null. A malformed host is null without a lookup. A found tenant is
    /// cached for 60 seconds and a miss for 5 seconds.
    /// </summary>
    Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>Drops the cached result for the host, so the next lookup reads the database (F-01 provisioning).</summary>
    void Invalidate(string host);
}
