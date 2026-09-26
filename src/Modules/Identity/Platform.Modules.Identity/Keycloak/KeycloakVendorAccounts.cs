using Microsoft.Extensions.Logging;
using Platform.Modules.Identity.Contracts;

namespace Platform.Modules.Identity.Keycloak;

/// <summary>
/// The Keycloak side of a vendor account (vendor spec V-3) through the Admin API: the realm role <c>vendor</c> and
/// organization membership, each recorded as added or already present so a failed registration removes only what it
/// added. The caller orders the steps and decides what to take back. Admin API failures surface as
/// <see cref="IdentityProviderException"/>, the contract's own type.
/// </summary>
internal sealed partial class KeycloakVendorAccounts(KeycloakAdminClient keycloak, ILogger<KeycloakVendorAccounts> logger) : IVendorAccounts
{
    public async Task<VendorAccountState> DescribeAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        try
        {
            var roles = await keycloak.RealmRolesAsync(userId, cancellationToken);
            var organizations = await keycloak.OrganizationAliasesOfAsync(userId, cancellationToken);
            return new VendorAccountState(roles.Contains(IdentityClaims.VendorRealmRole), organizations);
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            throw new IdentityProviderException("Keycloak did not describe the user's roles and organizations.", ex);
        }
    }

    public async Task<bool> GrantRoleAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        try
        {
            return await keycloak.AssignRealmRoleAsync(userId, IdentityClaims.VendorRealmRole, cancellationToken);
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            throw new IdentityProviderException("Keycloak did not grant the vendor role.", ex);
        }
    }

    public async Task<bool> AddToOrganizationAsync(string userId, string organizationAlias, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationAlias);
        try
        {
            return await keycloak.AddToOrganizationAsync(organizationAlias, userId, cancellationToken);
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            throw new IdentityProviderException("Keycloak did not add the user to the organization.", ex);
        }
    }

    public async Task RevokeAsync(VendorAccessGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.OrganizationAdded)
        {
            await BestEffortAsync(
                () => keycloak.RemoveFromOrganizationAsync(grant.OrganizationAlias, grant.UserId, cancellationToken), grant, "organization");
        }

        if (grant.RoleAdded)
        {
            await BestEffortAsync(
                () => keycloak.RemoveRealmRoleAsync(grant.UserId, IdentityClaims.VendorRealmRole, cancellationToken), grant, "role");
        }
    }

    // Undoing runs after something already failed; a step that fails here is logged (type only, N-10) for an operator,
    // and the other step still runs. Without the role or without the organization the Vendor policy stays closed.
    private async Task BestEffortAsync(Func<Task> undo, VendorAccessGrant grant, string what)
    {
        try
        {
            await undo();
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            RevokeFailed(logger, what, grant.UserId, grant.OrganizationAlias, ex.GetType().Name);
        }
    }

    private static bool IsProviderFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is KeycloakAdminException or HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    [LoggerMessage(Level = LogLevel.Error, Message = "The vendor {What} added for user {UserId} in organization {Alias} was not removed ({ErrorType}).")]
    private static partial void RevokeFailed(ILogger logger, string what, string userId, string alias, string errorType);
}

/// <summary>
/// The vendor account placeholder until <see cref="IdentityModule.AddKeycloakAdmin"/> replaces it: every call fails, so
/// a host without the Admin API can never register a vendor half way.
/// </summary>
internal sealed class UnavailableVendorAccounts : IVendorAccounts
{
    public Task<VendorAccountState> DescribeAsync(string userId, CancellationToken cancellationToken = default) => throw NotConfigured();

    public Task<bool> GrantRoleAsync(string userId, CancellationToken cancellationToken = default) => throw NotConfigured();

    public Task<bool> AddToOrganizationAsync(string userId, string organizationAlias, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task RevokeAsync(VendorAccessGrant grant, CancellationToken cancellationToken = default) => throw NotConfigured();

    private static InvalidOperationException NotConfigured() =>
        new("The Keycloak Admin API is not registered in this host (IdentityModule.AddKeycloakAdmin).");
}
