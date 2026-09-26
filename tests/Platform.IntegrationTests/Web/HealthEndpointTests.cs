using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Platform.IntegrationTests.Web;

public class HealthEndpointTests
{
    [Fact]
    public async Task Health_endpoint_answers_without_a_database_or_tenant()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Platform", "Host=unused;Database=unused"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
