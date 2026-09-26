using Platform.Shared.Tenancy;

namespace Platform.Modules.Tenancy.Contracts;

public interface ITenantDirectory
{
    /// <summary>The tenant that owns the host name, or null. Results, including misses, are cached for 60 seconds.</summary>
    Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default);
}
