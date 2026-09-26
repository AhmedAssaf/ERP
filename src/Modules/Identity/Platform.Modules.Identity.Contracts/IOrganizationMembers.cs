namespace Platform.Modules.Identity.Contracts;

/// <summary>
/// User counts of Keycloak organizations, for the platform console's tenant list (F-54 as narrowed): the members of the
/// tenant's organization, less those holding the realm role <c>vendor</c> (vendors join the organization, V-3, but are
/// not the tenant's users). Null means the count is not available (no Keycloak Admin API client configured, or Keycloak
/// did not answer); the console then shows a dash rather than a guessed number (D-12's rule).
/// </summary>
/// <remarks>
/// The console asks once per tenant on every load of its tenant list, so implementations should cache a known count
/// per organization for 60 seconds (the Identity module's does) and must be safe to call concurrently: the caller fans
/// out across tenants, a few at a time.
/// </remarks>
public interface IOrganizationMembers
{
    Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default);
}
