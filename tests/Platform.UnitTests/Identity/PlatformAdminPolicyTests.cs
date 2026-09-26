using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Modules.Identity;

namespace Platform.UnitTests.Identity;

/// <summary>D-2: the console needs the platform-admin realm role and an acr level of at least 2 (an OTP login).</summary>
public class PlatformAdminPolicyTests
{
    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    public async Task An_admin_with_acr_2_or_more_passes(string acr) =>
        (await AuthorizeAsync(Admin(new Claim("acr", acr)))).ShouldBeTrue();

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("mfa")]
    [InlineData("")]
    [InlineData(" 2")]
    public async Task An_admin_below_acr_2_or_with_an_unreadable_acr_is_refused(string acr) =>
        (await AuthorizeAsync(Admin(new Claim("acr", acr)))).ShouldBeFalse();

    [Fact]
    public async Task An_admin_without_an_acr_claim_is_refused() =>
        (await AuthorizeAsync(Admin())).ShouldBeFalse();

    [Fact]
    public async Task A_second_lower_acr_claim_is_refused() =>
        (await AuthorizeAsync(Admin(new Claim("acr", "2"), new Claim("acr", "1")))).ShouldBeFalse();

    [Fact]
    public async Task Acr_2_without_the_platform_admin_role_is_refused()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "x"), new Claim("acr", "2"), new Claim("roles", "tenant-admin")], "test"));

        (await AuthorizeAsync(user)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_anonymous_principal_with_the_claims_is_refused()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("acr", "2"), new Claim("roles", IdentityModule.PlatformAdminRole)]));

        (await AuthorizeAsync(user)).ShouldBeFalse();
    }

    private static ClaimsPrincipal Admin(params Claim[] extra) =>
        new(new ClaimsIdentity([new Claim("sub", "platform.admin"), new Claim("roles", IdentityModule.PlatformAdminRole), .. extra], "test"));

    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal user)
    {
        await using var services = new ServiceCollection().AddLogging().AddAuthorizationCore().BuildServiceProvider();
        var result = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, null, IdentityModule.PlatformAdminRequirements);
        return result.Succeeded;
    }
}
