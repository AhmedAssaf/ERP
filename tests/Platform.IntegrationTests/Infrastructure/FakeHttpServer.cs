using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// A minimal Kestrel double for an HTTP dependency (Keycloak's management port, or the web host's own /health;
/// plan task 3), used instead of HttpListener so no Windows URL-ACL reservation is needed for an arbitrary port.
/// </summary>
internal sealed class FakeHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeHttpServer(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public string BaseAddress { get; }

    /// <summary>
    /// Starts the double on a free loopback port. <paramref name="http2Only"/>: cleartext HTTP/2 with prior knowledge, as a
    /// gRPC client speaks it (the fake OTLP receiver).
    /// </summary>
    public static async Task<FakeHttpServer> StartAsync(Func<HttpContext, Task> handler, CancellationToken cancellationToken, bool http2Only = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (http2Only)
        {
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http2));
        }

        var app = builder.Build();
        // WebApplication itself has an instance Run(string?) that starts and blocks; call the middleware
        // extension explicitly so the RequestDelegate overload is the one picked.
        RunExtensions.Run(app, new RequestDelegate(handler));
        await app.StartAsync(cancellationToken);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeHttpServer(app, address.EndsWith('/') ? address : address + "/");
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
