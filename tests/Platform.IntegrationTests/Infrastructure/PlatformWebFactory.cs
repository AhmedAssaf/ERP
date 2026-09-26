using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>Keycloak settings for a host that keeps its real cookie and OpenID Connect handlers.</summary>
internal sealed record OidcSettings(string Authority, string ClientSecret);

/// <summary>
/// The web host in the Testing environment. By default a header-driven test scheme replaces cookie and OIDC sign-in;
/// pass <see cref="OidcSettings"/> to keep the real handlers against a Keycloak instance instead.
/// </summary>
internal sealed class PlatformWebFactory(string appConnectionString, OidcSettings? oidc = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Platform", appConnectionString);
        builder.UseSetting("Oidc:Authority", oidc?.Authority ?? "https://keycloak.invalid/realms/waslabid");
        builder.UseSetting("Oidc:ClientSecret", oidc?.ClientSecret ?? "unused-in-tests");
        if (oidc is not null)
        {
            builder.UseSetting("Oidc:RequireHttpsMetadata", "false");
            return;
        }

        builder.ConfigureTestServices(services => services.AddTestAuthentication());
    }

    public HttpClient ClientFor(string host) =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });
}
