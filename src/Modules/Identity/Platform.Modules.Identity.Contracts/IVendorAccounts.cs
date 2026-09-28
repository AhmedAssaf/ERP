namespace Platform.Modules.Identity.Contracts;

/// <summary>
/// What a registration changed in the identity provider, so a failed registration undoes exactly that: the realm role
/// <c>vendor</c> and membership of the organization with <see cref="OrganizationAlias"/>, each only when this attempt added it.
/// </summary>
public sealed record VendorAccessGrant(string UserId, string OrganizationAlias, bool RoleAdded, bool OrganizationAdded);

/// <summary>What the identity provider holds for an account before a registration: the realm role and the organizations.</summary>
public sealed record VendorAccountState(bool HoldsVendorRole, IReadOnlyList<string> OrganizationAliases);

/// <summary>
/// The Keycloak side of a vendor account (vendor spec V-3, ADR-0008): the realm role <c>vendor</c> and membership of each
/// related tenant's organization, through the Keycloak Admin API. Only the application grants the role; self-registration
/// never does. Each step reports whether it added something, so the caller decides what to take back; a failing step
/// surfaces as <see cref="IdentityProviderException"/> and leaves the steps before it as they were.
/// </summary>
public interface IVendorAccounts
{
    /// <summary>Whether the user holds the realm role <c>vendor</c>, and the aliases of every organization they are a member of.</summary>
    Task<VendorAccountState> DescribeAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Grants the realm role <c>vendor</c>. True when this call added it; false when the user already held it.</summary>
    Task<bool> GrantRoleAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the user to the organization with <paramref name="organizationAlias"/>. True when this call added them; false
    /// when they were already a member.
    /// </summary>
    Task<bool> AddToOrganizationAsync(string userId, string organizationAlias, CancellationToken cancellationToken = default);

    /// <summary>Undoes what <paramref name="grant"/> added, and nothing else. Best effort: a step that fails is logged.</summary>
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
