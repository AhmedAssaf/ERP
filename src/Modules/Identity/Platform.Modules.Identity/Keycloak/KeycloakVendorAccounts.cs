using Microsoft.Extensions.Logging;
using Platform.Modules.Identity.Contracts;

namespace Platform.Modules.Identity.Keycloak;

/// <summary>
/// The Keycloak side of a vendor account (vendor spec V-3) through the Admin API: the realm role <c>vendor</c> and
/// organization membership, each recorded as added or already present so a failed registration removes only what it
/// added. When adding the membership fails after the role was granted, the role is taken back before the error surfaces.
/// Admin API failures surface as <see cref="IdentityProviderException"/>, the contract's own type.
/// </summary>
internal sealed partial class KeycloakVendorAccounts(KeycloakAdminClient keycloak, ILogger<KeycloakVendorAccounts> logger) : IVendorAccounts
{
    public async Task<bool> BelongsToAnyOrganizationAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        try
        {
            return (await keycloak.OrganizationAliasesOfAsync(userId, cancellationToken)).Count > 0;
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            throw new IdentityProviderException("Keycloak did not list the user's organizations.", ex);
        }
    }

    public async Task<VendorAccessGrant> GrantAsync(string userId, string organizationAlias, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationAlias);
        bool roleAdded;
        try
        {
            roleAdded = await keycloak.AssignRealmRoleAsync(userId, IdentityClaims.VendorRealmRole, cancellationToken);
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            throw new IdentityProviderException("Keycloak did not grant the vendor role.", ex);
        }

        try
        {
            var organizationAdded = await keycloak.AddToOrganizationAsync(organizationAlias, userId, cancellationToken);
            return new VendorAccessGrant(userId, organizationAlias, roleAdded, organizationAdded);
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException or TaskCanceledException)
        {
            if (roleAdded)
            {
                await RevokeAsync(new VendorAccessGrant(userId, organizationAlias, RoleAdded: true, OrganizationAdded: false), CancellationToken.None);
            }

            if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

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
    public Task<bool> BelongsToAnyOrganizationAsync(string userId, CancellationToken cancellationToken = default) => throw NotConfigured();

    public Task<VendorAccessGrant> GrantAsync(string userId, string organizationAlias, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task RevokeAsync(VendorAccessGrant grant, CancellationToken cancellationToken = default) => throw NotConfigured();

    private static InvalidOperationException NotConfigured() =>
        new("The Keycloak Admin API is not registered in this host (IdentityModule.AddKeycloakAdmin).");
}
