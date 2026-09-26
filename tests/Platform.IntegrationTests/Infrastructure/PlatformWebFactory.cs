using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Platform.IntegrationTests.Infrastructure;

internal sealed class PlatformWebFactory(string appConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Platform", appConnectionString);
    }

    public HttpClient ClientFor(string host) =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });
}
