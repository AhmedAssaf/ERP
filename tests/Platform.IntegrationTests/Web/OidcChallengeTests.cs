using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// The host with its real cookie and OpenID Connect handlers against the repository's Keycloak realm (W-04). Keycloak
/// advertises pushed authorization requests, so the handler posts the parameters back-channel and the browser redirect
/// carries only the client id and a request URI; the pushed parameters are read from the handler's event.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class OidcChallengeTests(DatabaseFixture db, KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <remarks>
    /// The organization scope names the host's tenant (<c>organization:acme</c>), never the bare <c>organization</c>: with
    /// the bare scope Keycloak asks a user who belongs to two organizations (a vendor working with acme and beta, ADR-0008)
    /// to pick one on every sign-in, listing the other tenants' names, and the token then carries whichever they picked.
    /// Found by the vendor slice browser pass (plan task 7).
    /// </remarks>
    [Theory]
    [InlineData("acme")]
    [InlineData("beta")]
    public async Task Anonymous_request_is_redirected_to_keycloak_with_pkce_and_the_host_tenants_organization_scope(string slug)
    {
        OpenIdConnectMessage? pushed = null;
        await using var factory = new PlatformWebFactory(db.AppConnectionString, new OidcSettings(keycloak.Authority, KeycloakFixture.WebClientSecret));
        await using var observed = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                options.Events.OnPushAuthorization = context =>
                {
                    pushed = context.ProtocolMessage;
                    return Task.CompletedTask;
                })));
        using var client = observed.CreateClient(new() { BaseAddress = new Uri($"https://{slug}.localhost"), AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = response.Headers.Location.ShouldNotBeNull();
        location.GetLeftPart(UriPartial.Path).ShouldBe($"{keycloak.Authority}/protocol/openid-connect/auth");
        var query = QueryHelpers.ParseQuery(location.Query);
        query["client_id"].ToString().ShouldBe("waslabid-web");
        query["request_uri"].ToString().ShouldStartWith("urn:ietf:params:oauth:request_uri:");

        pushed.ShouldNotBeNull();
        pushed.ResponseType.ShouldBe("code");
        pushed.GetParameter("code_challenge_method").ShouldBe("S256");
        pushed.GetParameter("code_challenge").ShouldNotBeNullOrEmpty();
        pushed.RedirectUri.ShouldBe($"https://{slug}.localhost/signin-oidc");
        var scopes = pushed.Scope.Split(' ');
        scopes.ShouldContain($"organization:{slug}");
        scopes.ShouldNotContain("organization");
        scopes.ShouldNotContain("organization:*");
    }

    /// <remarks>
    /// Pins the Keycloak behaviour the named scope relies on, so an upgrade that changes it fails here: a member asking for
    /// <c>organization:acme</c> gets a token carrying the alias, and a user asking for an organization they do not belong
    /// to still gets a token, with no organization claim at all (never an error, a picker, or another tenant's alias).
    /// The tests-only client's password grant stands in for the code exchange; the scope handling is the same.
    /// </remarks>
    [Fact]
    public async Task Keycloak_puts_the_named_organization_in_the_token_only_for_a_member()
    {
        var member = new JsonWebToken(await keycloak.SignInAsync("acme.admin", "openid organization:acme", Ct));
        var nonMember = new JsonWebToken(await keycloak.SignInAsync("acme.admin", "openid organization:beta", Ct));

        OrganizationValues(member).ShouldBe(["acme"]);
        nonMember.Subject.ShouldBe(member.Subject);
        OrganizationValues(nonMember).ShouldBeEmpty();
    }

    private static string[] OrganizationValues(JsonWebToken token) =>
        [.. token.Claims.Where(c => c.Type == "organization").Select(c => c.Value)];
}
