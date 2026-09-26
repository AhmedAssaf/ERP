namespace Platform.Modules.Identity;

/// <summary>
/// The placeholder source until the Keycloak Admin API client exists (plan task 9, settings <c>KeycloakAdmin:*</c>): every
/// count is unknown. Registered with TryAdd as <see cref="IOrganizationMemberSource"/>, so task 9's client replaces it
/// by registering first or removing it; <see cref="CachingOrganizationMembers"/> stays in front either way.
/// </summary>
internal sealed class UnavailableOrganizationMembers : IOrganizationMemberSource
{
    public Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);
}
