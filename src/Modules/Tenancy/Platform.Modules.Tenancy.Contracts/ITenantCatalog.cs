namespace Platform.Modules.Tenancy.Contracts;

/// <summary>One tenant as the platform console lists it (F-54 as narrowed).</summary>
/// <param name="Status">"active" for every tenant until suspension exists (F-54 full, a later slice).</param>
public sealed record TenantSummary(Guid Id, string Slug, string OrganizationAlias, string PortalName, string Status);

/// <summary>
/// Every tenant, for the platform console only (spec 3.4). Reads through the security-definer function
/// <c>tenancy.list_tenants()</c>; call it only after the PlatformAdmin policy has passed.
/// </summary>
/// <remarks>
/// Trust model, as for <c>tenancy.resolve_host</c>: the database grants the function to the shared <c>erp_app</c> role,
/// so the gate is in code. The implementation is scoped and throws <see cref="InvalidOperationException"/> unless the
/// scope was marked as a platform request (<c>IPlatformRequestContext</c>): the web host marks requests on the platform
/// host and circuits opened from them; tenant requests, the worker and the migrator never are.
/// </remarks>
public interface ITenantCatalog
{
    /// <summary>All tenants, ordered by portal name.</summary>
    Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken = default);
}
