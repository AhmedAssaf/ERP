using Platform.Modules.Identity.Contracts;

namespace Platform.Modules.Identity;

/// <summary>
/// The placeholder until the Keycloak Admin API client exists (plan task 9, settings <c>KeycloakAdmin:*</c>): every count
/// is unknown. Registered with TryAdd, so task 9's implementation replaces it by registering first or removing it.
/// </summary>
internal sealed class UnavailableOrganizationMembers : IOrganizationMembers
{
    public Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);
}
