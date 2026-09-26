using System.Net;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Web;

[Collection(DatabaseCollection.Name)]
public class TenantResolutionTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unknown_host_gets_404_before_anything_else()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("nobody.localhost");

        var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Known_host_is_served()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldNotBe(HttpStatusCode.NotFound);
    }
}
