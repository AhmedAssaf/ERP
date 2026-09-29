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

    /// <summary>The name and email of every account, by user id; an account missing here has none.</summary>
    public ConcurrentDictionary<string, VendorAccountProfile> Profiles { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, reading a profile fails as Keycloak failing would.</summary>
    public bool FailProfile { get; set; }

    /// <summary>The users granted the realm role <c>vendor</c> by <see cref="GrantRoleAsync"/>.</summary>
    public ConcurrentQueue<string> Granted { get; } = new();

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
        Granted.Enqueue(userId);
        if (OnGrantRole is not null)
        {
            await OnGrantRole(userId);
        }

        return !State.HoldsVendorRole;
    }

    public Task<bool> AddToOrganizationAsync(string userId, string organizationAlias, CancellationToken cancellationToken = default)
    {
        Steps.Enqueue("add-organization");
        return FailOrganization
            ? throw new IdentityProviderException("Keycloak did not add the user to the organization.", new HttpRequestException("forced"))
            : Task.FromResult(!State.OrganizationAliases.Contains(organizationAlias, StringComparer.Ordinal));
    }

    public Task RevokeAsync(VendorAccessGrant grant, CancellationToken cancellationToken = default)
    {
        Steps.Enqueue("revoke");
        Revoked.Enqueue(grant);
        return Task.CompletedTask;
    }

    public Task<VendorAccountProfile?> ProfileAsync(string userId, CancellationToken cancellationToken = default)
    {
        Steps.Enqueue("profile");
        return FailProfile
            ? throw new IdentityProviderException("Keycloak did not describe the user's name and email.", new HttpRequestException("forced"))
            : Task.FromResult(Profiles.TryGetValue(userId, out var profile) ? profile : null);
    }
}
