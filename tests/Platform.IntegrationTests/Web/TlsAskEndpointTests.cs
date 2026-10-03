using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// The pilot's on-demand TLS (docs/19): Caddy asks <c>GET /internal/tls-ask?domain=&lt;host&gt;</c> before it obtains a
/// certificate, and issues one only on a 200. The endpoint answers only on its own listener (<c>TlsAsk:Port</c>), which no
/// Caddy site block proxies to; on the public listener the path is a 404 like any unknown path. A host is allowed when it is
/// one label under <c>TlsAsk:TenantBaseDomain</c> and a tenant owns it (<c>tenancy.tenant_hosts</c>); the platform host never.
/// The test server has no sockets, so each request sets the connection's local port itself.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TlsAskEndpointTests(DatabaseFixture db)
{
    private const int AskPort = 8081;
    private const int PublicPort = 8080;
    private const string AskPath = "/internal/tls-ask";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_known_tenant_host_is_allowed_on_the_ask_listener()
    {
        await using var factory = Factory();

        var status = await AskAsync(factory, TestTenants.Acme.Slug + ".localhost");

        status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("ACME.LOCALHOST")]
    [InlineData("acme.localhost.")]
    [InlineData("Beta.Localhost.")]
    public async Task Upper_case_and_a_trailing_dot_are_normalised(string domain)
    {
        await using var factory = Factory();

        var status = await AskAsync(factory, domain);

        status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("nobody.localhost")] // under the base domain, but no tenant owns it
    [InlineData("localhost")] // the base domain itself
    [InlineData("x.acme.localhost")] // two labels under the base domain
    [InlineData("acme.localhost.evil.example")] // a tenant host as a prefix of another domain
    [InlineData("evilacme.localhost")]
    [InlineData("acme.localhost..")]
    [InlineData(".acme.localhost")]
    [InlineData("-acme.localhost")]
    [InlineData("acme.localhost:443")]
    [InlineData("acme_x.localhost")]
    [InlineData("acme.localhost/x")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("10.0.0.1.localhost")]
    [InlineData("%20acme.localhost")]
    [InlineData("")]
    public async Task Anything_but_a_tenant_host_one_label_under_the_base_domain_is_refused(string domain)
    {
        await using var factory = Factory();

        var status = await AskAsync(factory, Uri.UnescapeDataString(domain));

        status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_host_longer_than_a_dns_name_is_refused()
    {
        await using var factory = Factory();

        var status = await AskAsync(factory, new string('a', 250) + ".localhost");

        status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_tenant_host_outside_the_configured_base_domain_is_refused()
    {
        // acme.localhost is in tenancy.tenant_hosts, but certificates are issued only under the configured base domain
        // (custom domains are F-03 and not part of the pilot).
        await using var factory = Factory(("TlsAsk:TenantBaseDomain", "example.invalid"));

        var status = await AskAsync(factory, "acme.localhost");

        status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_platform_host_is_refused_even_if_it_looks_like_a_tenant_host()
    {
        // Platform:Host has its own certificate from its own site block; the ask endpoint never vouches for it.
        await using var factory = Factory(("Platform:Host", "acme.localhost"));

        var status = await AskAsync(factory, "acme.localhost");

        status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_a_domain_or_with_two_the_request_is_bad()
    {
        await using var factory = Factory();

        (await SendAsync(factory, AskPort, HttpMethods.Get, AskPath, string.Empty)).ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(factory, AskPort, HttpMethods.Get, AskPath, "?domain=acme.localhost&domain=beta.localhost"))
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/internal/tls-ask")]
    [InlineData("/INTERNAL/TLS-ASK")]
    [InlineData("/internal/tls-ask/")]
    [InlineData("/internal/anything")]
    public async Task On_the_public_listener_the_ask_path_is_a_404_even_on_a_tenant_host(string path)
    {
        await using var factory = Factory();

        var status = await SendAsync(factory, PublicPort, HttpMethods.Get, path, "?domain=acme.localhost", host: "acme.localhost");

        status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_an_ask_port_the_path_is_a_404_on_every_listener()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);

        var status = await SendAsync(factory, AskPort, HttpMethods.Get, AskPath, "?domain=acme.localhost", host: "acme.localhost");

        status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/alive")]
    [InlineData("GET", "/platform")]
    [InlineData("GET", "/internal/tls-ask/x")]
    [InlineData("POST", "/internal/tls-ask")]
    [InlineData("HEAD", "/internal/tls-ask")]
    public async Task The_ask_listener_serves_nothing_else(string method, string path)
    {
        await using var factory = Factory();

        // Even with a tenant's Host header: the ask listener never reaches tenant resolution or the pages.
        var status = await SendAsync(factory, AskPort, method, path, "?domain=acme.localhost", host: "acme.localhost");

        status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lookups_beyond_the_rate_are_refused_with_429()
    {
        await using var factory = Factory(("TlsAsk:RequestsPerSecond", "1"));

        var first = await AskAsync(factory, "acme.localhost");
        var second = await AskAsync(factory, "acme.localhost");

        first.ShouldBe(HttpStatusCode.OK);
        second.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("TlsAsk:TenantBaseDomain", "")]
    [InlineData("TlsAsk:TenantBaseDomain", "*.example.sa")]
    [InlineData("TlsAsk:TenantBaseDomain", "https://example.sa")]
    [InlineData("TlsAsk:TenantBaseDomain", ".example.sa")]
    [InlineData("TlsAsk:Port", "0")]
    [InlineData("TlsAsk:Port", "70000")]
    [InlineData("TlsAsk:Port", "eighty")]
    [InlineData("TlsAsk:RequestsPerSecond", "0")]
    public void An_unusable_ask_setting_stops_the_host(string key, string value)
    {
        using var factory = Factory((key, value));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain(key);
    }

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings) =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TlsAsk:Port", AskPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("TlsAsk:TenantBaseDomain", "localhost");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static Task<HttpStatusCode> AskAsync(WebApplicationFactory<Program> factory, string domain) =>
        SendAsync(factory, AskPort, HttpMethods.Get, AskPath, QueryString.Create("domain", domain).Value!);

    /// <summary>
    /// Sends one request as if it arrived on <paramref name="localPort"/>. Caddy's ask call goes straight to <c>web:8081</c>,
    /// so its Host header is that address unless a test names a tenant host.
    /// </summary>
    private static async Task<HttpStatusCode> SendAsync(
        WebApplicationFactory<Program> factory, int localPort, string method, string path, string query, string host = "web:8081")
    {
        var context = await factory.Server.SendAsync(
            c =>
            {
                c.Request.Method = method;
                c.Request.Scheme = "http";
                c.Request.Host = new HostString(host);
                c.Request.Path = path;
                c.Request.QueryString = new QueryString(string.IsNullOrEmpty(query) ? null : query);
                c.Connection.LocalPort = localPort;
                c.Connection.RemoteIpAddress = IPAddress.Parse("172.30.10.2");
            },
            Ct);
        return (HttpStatusCode)context.Response.StatusCode;
    }
}
