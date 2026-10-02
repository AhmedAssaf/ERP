using System.Net;
using Microsoft.AspNetCore.Http;
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

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_bypass_covers_only_the_exact_health_path()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("nobody.localhost");

        var response = await client.GetAsync(new Uri("/health/x", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>W-10 (O-15): liveness answers on every host, before tenant resolution, as the exact <c>/health</c> does.</summary>
    [Theory]
    [InlineData("acme.localhost")]
    [InlineData(PlatformWebFactory.PlatformHost)]
    [InlineData("nobody.localhost")]
    public async Task Alive_is_served_on_tenant_and_platform_hosts_and_on_an_unknown_host(string host)
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor(host);

        var response = await client.GetAsync(new Uri("/alive", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task A_path_under_alive_is_an_ordinary_tenant_path()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("nobody.localhost");

        var response = await client.GetAsync(new Uri("/alive/x", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Empty_host_gets_404()
    {
        // HttpClient and TestServer's client handler both fill in a missing Host header, so set it on the context.
        await using var factory = new PlatformWebFactory(db.AppConnectionString);

        var context = await factory.Server.SendAsync(
            c =>
            {
                c.Request.Method = HttpMethods.Get;
                c.Request.Path = "/";
                c.Request.Host = default;
            },
            Ct);

        context.Request.Host.HasValue.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }
}
