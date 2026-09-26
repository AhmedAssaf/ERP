using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task Anonymous_request_is_redirected_to_keycloak_with_pkce_and_the_organization_scope()
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
        using var client = observed.CreateClient(new() { BaseAddress = new Uri("https://acme.localhost"), AllowAutoRedirect = false });

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
        pushed.RedirectUri.ShouldBe("https://acme.localhost/signin-oidc");
        pushed.Scope.Split(' ').ShouldContain("organization");
    }
}
