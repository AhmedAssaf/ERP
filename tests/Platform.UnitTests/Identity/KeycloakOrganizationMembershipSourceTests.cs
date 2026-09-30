using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Modules.Identity;
using Platform.Modules.Identity.Keycloak;

namespace Platform.UnitTests.Identity;

/// <summary>
/// W-21 review: whatever Keycloak (or a proxy in front of it) sends back, the membership source answers
/// <see cref="OrganizationMembership.Unavailable"/> rather than throwing. A thrown exception would fault the check shared
/// by every concurrent request of that user (a 500 for each) and never start the back-off.
/// </summary>
public sealed class KeycloakOrganizationMembershipSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("<html><body>Bad gateway page served with 200</body></html>")]
    [InlineData("""{"id":"u1","enabled":"not-a-boolean"}""")]
    public async Task An_answer_that_is_not_the_expected_json_counts_as_keycloak_unavailable(string body)
    {
        var source = Source(new BodyHandler(body));

        (await source.CheckAsync("acme", "u1", Ct)).ShouldBe(OrganizationMembership.Unavailable);
        (await source.CheckAccountAsync("u1", Ct)).ShouldBe(OrganizationMembership.Unavailable);
    }

    private static KeycloakOrganizationMembershipSource Source(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<KeycloakAdminOptions>().Configure(o =>
        {
            o.BaseUrl = "http://keycloak.test";
            o.ClientSecret = "secret";
        });
        services.AddSingleton<KeycloakAdminState>();
        services.AddHttpClient<KeycloakAdminClient>(http => http.BaseAddress = new Uri("http://keycloak.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return new KeycloakOrganizationMembershipSource(
            provider,
            provider.GetRequiredService<IOptions<KeycloakAdminOptions>>(),
            NullLogger<KeycloakOrganizationMembershipSource>.Instance);
    }

    /// <summary>A valid token, then <paramref name="body"/> with status 200 for every Admin API call.</summary>
    private sealed class BodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                        ? """{"access_token":"token","expires_in":300,"token_type":"Bearer"}"""
                        : body,
                    Encoding.UTF8,
                    "application/json"),
            });
    }
}
