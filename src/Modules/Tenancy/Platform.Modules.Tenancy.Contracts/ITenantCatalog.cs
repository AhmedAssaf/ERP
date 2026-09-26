namespace Platform.Modules.Tenancy.Contracts;

/// <summary>One tenant as the platform console lists it (F-54 as narrowed).</summary>
/// <param name="Status">"active" for every tenant until suspension exists (F-54 full, a later slice).</param>
public sealed record TenantSummary(Guid Id, string Slug, string OrganizationAlias, string PortalName, string Status);

/// <summary>
/// Every tenant, for the platform console only (spec 3.4). Reads through the security-definer function
/// <c>tenancy.list_tenants()</c>; call it only after the PlatformAdmin policy has passed.
/// </summary>
public interface ITenantCatalog
{
    /// <summary>All tenants, ordered by portal name.</summary>
    Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken = default);
}
