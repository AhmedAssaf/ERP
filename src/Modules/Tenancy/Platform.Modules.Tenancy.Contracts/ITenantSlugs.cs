namespace Platform.Modules.Tenancy.Contracts;

/// <summary>
/// Every tenant's slug by id, for the platform's own jobs: the worker's usage job labels its counts with the slug (W-10,
/// spec 6.1 and 6.4). Slugs only, which are public anyway (each is its tenant's host name), never portal names or
/// organization aliases; <see cref="ITenantCatalog"/> stays the console's only full list.
/// </summary>
/// <remarks>
/// Reads through <c>tenancy.list_tenants()</c>, which refuses a session with a tenant or vendor context. This module's
/// data source carries no context of its own, so the implementation refuses in code (<see cref="InvalidOperationException"/>)
/// a scope that has a tenant or a vendor context, as the database would. The worker's scopes have neither.
/// </remarks>
public interface ITenantSlugs
{
    Task<IReadOnlyDictionary<Guid, string>> ListAsync(CancellationToken cancellationToken = default);
}
