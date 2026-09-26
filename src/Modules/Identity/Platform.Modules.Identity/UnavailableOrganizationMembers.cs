namespace Platform.Modules.Identity;

/// <summary>
/// The source when the host has not wired the Keycloak Admin API (<see cref="IdentityModule.AddKeycloakAdmin"/>): every
/// count is unknown. <c>AddKeycloakAdmin</c> replaces it with <see cref="Keycloak.KeycloakOrganizationMemberSource"/>;
/// <see cref="CachingOrganizationMembers"/> stays in front either way.
/// </summary>
internal sealed class UnavailableOrganizationMembers : IOrganizationMemberSource
{
    public Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);
}
