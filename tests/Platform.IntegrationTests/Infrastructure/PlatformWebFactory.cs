using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Platform.IntegrationTests.Infrastructure;

internal sealed class PlatformWebFactory(string appConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Platform", appConnectionString);
        builder.UseSetting("Oidc:Authority", "https://keycloak.invalid/realms/waslabid");
        builder.UseSetting("Oidc:ClientSecret", "unused-in-tests");
        builder.ConfigureTestServices(services => services.AddTestAuthentication());
    }

    public HttpClient ClientFor(string host) =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });
}
