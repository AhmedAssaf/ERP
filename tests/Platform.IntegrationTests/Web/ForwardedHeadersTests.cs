using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-24: behind Caddy the host takes the scheme and the client address from <c>X-Forwarded-Proto</c> and
/// <c>X-Forwarded-For</c>, but only when the request comes from a configured proxy address or network; from anywhere else
/// the headers are ignored. The probe endpoint runs after the host's whole pipeline and echoes what the request became.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ForwardedHeadersTests(DatabaseFixture db)
{
    private const string CaddyNetwork = "172.18.0.0/16";
    private const string CaddyAddress = "172.18.0.5";
    private const string ClientAddress = "203.0.113.7";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_from_a_known_proxy_network_takes_the_forwarded_scheme_and_client_address()
    {
        await using var factory = Factory(("ForwardedHeaders:KnownNetworks:0", CaddyNetwork));

        var seen = await ProbeAsync(factory, from: CaddyAddress, proto: "https", forwardedFor: ClientAddress);

        seen.ShouldBe($"https {ClientAddress}");
    }

    [Fact]
    public async Task A_request_from_a_known_proxy_address_takes_the_forwarded_scheme_and_client_address()
    {
        await using var factory = Factory(("ForwardedHeaders:KnownProxies:0", CaddyAddress));

        var seen = await ProbeAsync(factory, from: CaddyAddress, proto: "https", forwardedFor: ClientAddress);

        seen.ShouldBe($"https {ClientAddress}");
    }

    [Fact]
    public async Task Forwarded_headers_from_an_address_outside_the_known_networks_are_ignored()
    {
        await using var factory = Factory(("ForwardedHeaders:KnownNetworks:0", CaddyNetwork));

        var seen = await ProbeAsync(factory, from: "198.51.100.9", proto: "https", forwardedFor: "10.1.2.3");

        seen.ShouldBe("http 198.51.100.9");
    }

    [Fact]
    public async Task Only_the_address_the_proxy_appended_is_taken_from_a_spoofed_forwarded_for()
    {
        await using var factory = Factory(("ForwardedHeaders:KnownNetworks:0", CaddyNetwork));

        var seen = await ProbeAsync(factory, from: CaddyAddress, proto: "https", forwardedFor: $"10.9.9.9, {ClientAddress}");

        seen.ShouldBe($"https {ClientAddress}");
    }

    [Fact]
    public async Task Without_a_known_proxy_the_testing_host_trusts_no_forwarded_header()
    {
        await using var factory = Factory();

        var seen = await ProbeAsync(factory, from: "127.0.0.1", proto: "https", forwardedFor: ClientAddress);

        seen.ShouldBe("http 127.0.0.1");
    }

    [Fact]
    public async Task Forwarded_host_is_never_taken_so_the_tenant_comes_from_the_host_header()
    {
        await using var factory = Factory(("ForwardedHeaders:KnownNetworks:0", CaddyNetwork));
        using var client = Client(factory);
        using var request = Request(from: CaddyAddress, proto: "https", forwardedFor: ClientAddress);
        request.Headers.Add("X-Forwarded-Host", "beta.localhost");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues(EdgeProbe.HostHeader).ShouldBe(["acme.localhost"]);
    }

    /// <summary>The local Compose stack is unchanged: Caddy's scheme is trusted from any address, its client address is not.</summary>
    [Fact]
    public async Task Development_keeps_trusting_the_local_caddy_scheme_only()
    {
        await using var factory = Factory(environment: "Development");

        var seen = await ProbeAsync(factory, from: "192.168.65.1", proto: "https", forwardedFor: ClientAddress);

        seen.ShouldBe("https 192.168.65.1");
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-address")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "172.18.0.0")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "0.0.0.0/0")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "::/0")]
    public void A_known_proxy_setting_that_is_unusable_or_trusts_every_address_stops_the_host(string key, string value)
    {
        using var factory = Factory((key, value));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain(key[..key.LastIndexOf(':')]);
    }

    [Fact]
    public void Production_host_without_a_known_proxy_does_not_start()
    {
        using var factory = new PlatformWebFactory("Host=unused;Database=unused", environment: "Production")
            .WithWebHostBuilder(builder => builder.UseSetting("ForwardedHeaders:KnownProxies:0", string.Empty));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain("ForwardedHeaders:KnownProxies");
        refused.Message.ShouldContain("ForwardedHeaders:KnownNetworks");
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings) => Factory("Testing", settings);

    private WebApplicationFactory<Program> Factory(string environment, params (string Key, string Value)[] settings) =>
        new PlatformWebFactory(db.AppConnectionString, environment: environment).WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, EdgeProbe>());
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });

    private static HttpRequestMessage Request(string from, string proto, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, EdgeProbe.Path);
        request.Headers.Add(EdgeProbe.RemoteAddressHeader, from);
        request.Headers.Add("X-Forwarded-Proto", proto);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    private static async Task<string> ProbeAsync(WebApplicationFactory<Program> factory, string from, string proto, string forwardedFor)
    {
        using var client = Client(factory);
        using var request = Request(from, proto, forwardedFor);
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <summary>
    /// Sets the connection's remote address from a test header before the host's pipeline (the test server has none), and
    /// maps an anonymous endpoint after it that echoes the scheme and client address the request ended up with.
    /// </summary>
    private sealed class EdgeProbe : IStartupFilter
    {
        public const string Path = "/test/edge";
        public const string RemoteAddressHeader = "X-Test-Remote-Address";
        public const string HostHeader = "X-Test-Host";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteAddressHeader, out var remote))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(remote.ToString());
                }

                return nextMiddleware(context);
            });
            next(app);
            var endpoints = app.Properties.TryGetValue("__EndpointRouteBuilder", out var value) && value is IEndpointRouteBuilder routeBuilder
                ? routeBuilder
                : throw new InvalidOperationException("The host did not expose its endpoint route builder.");
            endpoints.MapGet(Path, (HttpContext context) =>
            {
                context.Response.Headers[HostHeader] = context.Request.Host.Value;
                return Results.Text($"{context.Request.Scheme} {context.Connection.RemoteIpAddress}");
            }).AllowAnonymous();
        };
    }
}
