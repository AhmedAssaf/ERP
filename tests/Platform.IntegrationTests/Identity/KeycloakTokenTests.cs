using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity;
using Platform.Modules.Tenancy.Contracts;

namespace Platform.IntegrationTests.Identity;

/// <summary>W-04: a login through Keycloak carries the organization, and the host maps it to the TenantContext.</summary>
[Collection(DatabaseCollection.Name)]
public class KeycloakTokenTests(DatabaseFixture db, KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_token_carries_the_organization_and_maps_to_the_hosts_tenant()
    {
        var token = new JsonWebToken(await keycloak.SignInAsync("acme.admin", Ct));
        var user = new ClaimsPrincipal(new ClaimsIdentity(token.Claims, "keycloak"));

        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(null);
        var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
        var acme = await directory.FindByHostAsync("acme.localhost", Ct);
        var beta = await directory.FindByHostAsync("beta.localhost", Ct);

        acme.ShouldNotBeNull();
        beta.ShouldNotBeNull();
        OrganizationClaims.BelongsTo(user, acme).ShouldBeTrue();
        OrganizationClaims.BelongsTo(user, beta).ShouldBeFalse();
        user.FindFirst("locale")?.Value.ShouldBe("ar");
        // The seeded member row is bound by verified email on first sign-in (F-07 dev seed), so the id token must carry both.
        user.FindFirst("email")?.Value.ShouldBe("admin@acme.waslabid.test");
        user.FindFirst("email_verified")?.Value.ShouldBe("true");
    }
}
