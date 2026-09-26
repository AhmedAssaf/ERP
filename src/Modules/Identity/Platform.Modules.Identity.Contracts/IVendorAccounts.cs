namespace Platform.Modules.Identity.Contracts;

/// <summary>What <see cref="IVendorAccounts.GrantAsync"/> changed, so a failed registration undoes exactly that.</summary>
public sealed record VendorAccessGrant(string UserId, string OrganizationAlias, bool RoleAdded, bool OrganizationAdded);

/// <summary>
/// The Keycloak side of a vendor account (vendor spec V-3, ADR-0008): the realm role <c>vendor</c> and membership of each
/// related tenant's organization, through the Keycloak Admin API. Only the application grants the role; self-registration
/// never does.
/// </summary>
public interface IVendorAccounts
{
    /// <summary>True when the user is a member of any organization (tenant staff, or a vendor already related to a tenant).</summary>
    Task<bool> BelongsToAnyOrganizationAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Grants the realm role <c>vendor</c> and adds the user to the organization with <paramref name="organizationAlias"/>.</summary>
    Task<VendorAccessGrant> GrantAsync(string userId, string organizationAlias, CancellationToken cancellationToken = default);

    /// <summary>Undoes what <paramref name="grant"/> added, and nothing else.</summary>
    Task RevokeAsync(VendorAccessGrant grant, CancellationToken cancellationToken = default);
}

/// <summary>
/// The identity provider did not do what <see cref="IVendorAccounts"/> asked (unreachable, timed out, or refused). The
/// message names the step, never a secret or an address (N-10); the inner exception is the provider client's own.
/// </summary>
public sealed class IdentityProviderException : Exception
{
    public IdentityProviderException()
    {
    }

    public IdentityProviderException(string message)
        : base(message)
    {
    }

    public IdentityProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
