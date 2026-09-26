namespace Platform.Modules.Identity.Contracts;

/// <summary>
/// Member counts of Keycloak organizations, for the platform console's tenant list (F-54 as narrowed). Null means the
/// count is not available (no Keycloak Admin API client configured, or Keycloak did not answer); the console then shows
/// a dash rather than a guessed number (D-12's rule).
/// </summary>
public interface IOrganizationMembers
{
    Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default);
}
