using System.Net;
using Microsoft.AspNetCore.Hosting;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// W-34: outside Development and Testing the web host needs <c>ConnectionStrings:Redis</c>, or the duplicate-CR limits
/// would silently fall back to one count per instance; it refuses to start, naming the setting and never a value
/// (N-10). In Development and Testing it starts without it and keeps the limits in process memory.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RedisRequiredSettingTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_web_host_does_not_start_in_production_without_the_redis_connection_string()
    {
        using var factory = new PlatformWebFactory(db.AppConnectionString, environment: "Production")
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Redis", string.Empty));

        var refused = Should.Throw<Exception>(() => factory.Server);

        var messages = string.Join('\n', Chain(refused).Select(e => e.Message));
        messages.ShouldContain("Setting 'ConnectionStrings:Redis' is not configured.");
        messages.ShouldNotContain(db.AppConnectionString);
    }

    [Fact]
    public async Task The_web_host_starts_in_production_with_the_redis_connection_string()
    {
        using var factory = new PlatformWebFactory(db.AppConnectionString, environment: "Production");

        using var client = factory.ClientFor("acme.localhost");
        (await client.GetAsync("/alive", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task The_web_host_starts_without_the_redis_connection_string_in_development_and_testing(string environment)
    {
        using var factory = new PlatformWebFactory(db.AppConnectionString, environment: environment)
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Redis", string.Empty));

        using var client = factory.CreateClient();
        (await client.GetAsync("/alive", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
