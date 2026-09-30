using System.Collections.Concurrent;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// An in-memory Keycloak side of vendor accounts (<see cref="IVendorAccounts"/>) for registration tests that need no
/// Keycloak: the account starts as <see cref="State"/>, each step can be made to fail, and every revocation is recorded.
/// </summary>
internal sealed class FakeVendorAccounts : IVendorAccounts
{
    /// <summary>What Keycloak holds for the user before the registration.</summary>
    public VendorAccountState State { get; set; } = new(HoldsVendorRole: false, OrganizationAliases: []);

    /// <summary>Runs when the role is granted, before the answer (a parallel registration winning the race, say).</summary>
    public Func<string, Task>? OnGrantRole { get; set; }

    /// <summary>When set, adding the organization fails as Keycloak failing would.</summary>
    public bool FailOrganization { get; set; }

    /// <summary>
    /// Runs when the organization is added, before the answer: a parallel join of the same vendor winning the race, say,
    /// between the caller's relationship check and its database step.
    /// </summary>
    public Func<string, Task>? OnAddOrganization { get; set; }

    public ConcurrentQueue<VendorAccessGrant> Revoked { get; } = new();

    public ConcurrentQueue<string> Steps { get; } = new();

    public Task<VendorAccountState> DescribeAsync(string userId, CancellationToken cancellationToken = default)
    {
        Steps.Enqueue("describe");
        return Task.FromResult(State);
    }

    public async Task<bool> GrantRoleAsync(string userId, CancellationToken cancellationToken = default)
    {
        Steps.Enqueue("grant-role");
        if (OnGrantRole is not null)
        {
            await OnGrantRole(userId);
        }

        return !State.HoldsVendorRole;
    }

    public async Task<bool> AddToOrganizationAsync(string userId, string organizationAlias, CancellationToken cancellationToken = default)
    {
        Steps.Enqueue("add-organization");
        if (FailOrganization)
        {
            throw new IdentityProviderException("Keycloak did not add the user to the organization.", new HttpRequestException("forced"));
        }

        if (OnAddOrganization is not null)
        {
            await OnAddOrganization(userId);
        }

        return !State.OrganizationAliases.Contains(organizationAlias, StringComparer.Ordinal);
    }

    public Task RevokeAsync(VendorAccessGrant grant, CancellationToken cancellationToken = default)
    {
        Steps.Enqueue("revoke");
        Revoked.Enqueue(grant);
        return Task.CompletedTask;
    }
}
