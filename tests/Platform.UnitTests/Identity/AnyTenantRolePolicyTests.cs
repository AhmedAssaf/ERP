using Platform.Modules.Identity;
using Platform.Modules.Identity.Contracts;

namespace Platform.UnitTests.Identity;

/// <summary>
/// <see cref="IdentityModule.AnyTenantRolePolicy"/> (vendor plan task 5, the VendorManager policy): built only from one or
/// more known tenant roles, so a typo can never become a policy that nobody, or everybody, passes.
/// </summary>
public sealed class AnyTenantRolePolicyTests
{
    [Fact]
    public void An_empty_role_list_is_refused()
    {
        Should.Throw<ArgumentException>(() => IdentityModule.AnyTenantRolePolicy());
    }

    [Theory]
    [InlineData("contracts_officer")]
    [InlineData("vendor")]
    [InlineData("")]
    public void An_unknown_role_is_refused_and_named(string role)
    {
        var ex = Should.Throw<ArgumentException>(() => IdentityModule.AnyTenantRolePolicy(TenantRoles.TenantAdmin, role));
        if (role.Length > 0)
        {
            ex.Message.ShouldContain(role);
        }
    }

    [Fact]
    public void Known_roles_build_a_policy_that_requires_a_signed_in_staff_member()
    {
        var policy = IdentityModule.AnyTenantRolePolicy(TenantRoles.TenantAdmin, TenantRoles.ContractsOfficer);

        policy.Requirements.Count.ShouldBeGreaterThan(IdentityModule.TenantStaffPolicy.Requirements.Count);
        foreach (var requirement in IdentityModule.TenantStaffPolicy.Requirements)
        {
            policy.Requirements.ShouldContain(requirement);
        }
    }
}
