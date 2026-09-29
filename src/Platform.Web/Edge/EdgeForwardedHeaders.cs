using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Platform.Web.Edge;

/// <summary>
/// W-24: which forwarded headers the host believes, and from whom. Caddy terminates TLS and proxies to the app, so the
/// request's scheme and client address arrive in <c>X-Forwarded-Proto</c> and <c>X-Forwarded-For</c>.
/// <list type="bullet">
/// <item>Development (the local Compose stack): only the scheme, from any address, as before; Caddy runs in Docker and
/// reaches the host through a gateway address that differs between machines.</item>
/// <item>Everywhere else: the scheme and the client address, only from the addresses in
/// <c>ForwardedHeaders:KnownProxies</c> and the networks in <c>ForwardedHeaders:KnownNetworks</c> (CIDR). With one hop
/// (<see cref="ForwardedHeadersOptions.ForwardLimit"/> 1) only the last <c>X-Forwarded-For</c> entry, the one Caddy
/// appended, is taken, so an address a client wrote into the header itself is never believed. Outside Testing the host
/// refuses to start without either setting, and a network that matches every address (<c>/0</c>) is refused.</item>
/// </list>
/// <c>X-Forwarded-Host</c> is never taken: Caddy passes the client's <c>Host</c> through, and the tenant is resolved from it.
/// </summary>
internal static class EdgeForwardedHeaders
{
    public const string KnownProxiesSetting = "ForwardedHeaders:KnownProxies";
    public const string KnownNetworksSetting = "ForwardedHeaders:KnownNetworks";

    public static IServiceCollection AddEdgeForwardedHeaders(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment())
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });
            return services;
        }

        var proxies = Values(configuration, KnownProxiesSetting).Select(ParseProxy).ToList();
        var networks = Values(configuration, KnownNetworksSetting).Select(ParseNetwork).ToList();
        if (proxies.Count == 0 && networks.Count == 0 && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                $"Neither '{KnownProxiesSetting}' nor '{KnownNetworksSetting}' is configured. Outside Development the host believes "
                + "forwarded headers only from its edge proxy (Caddy); name its address or network.");
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // The middleware checks the sender only when a list is non-empty: with both lists empty it would believe every
            // address. With no edge configured (Testing only) no forwarded header is processed at all.
            options.ForwardedHeaders = proxies.Count == 0 && networks.Count == 0
                ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            // The defaults trust loopback; only the configured edge is trusted here.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            proxies.ForEach(options.KnownProxies.Add);
            networks.ForEach(options.KnownIPNetworks.Add);
        });
        return services;
    }

    private static List<string> Values(IConfiguration configuration, string setting)
    {
        var section = configuration.GetSection(setting);
        // A list (KnownProxies:0, KnownProxies:1, or KnownProxies__0 in the environment) or one comma-separated value.
        var values = section.GetChildren().Select(c => c.Value).Append(section.Value);
        return values
            .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
    }

    private static IPAddress ParseProxy(string value) =>
        IPAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException($"Setting '{KnownProxiesSetting}' has '{value}', which is not an IP address.");

    private static IPNetwork ParseNetwork(string value)
    {
        if (!IPNetwork.TryParse(value, out var network))
        {
            throw new InvalidOperationException(
                $"Setting '{KnownNetworksSetting}' has '{value}', which is not a network in CIDR form (for example 172.18.0.0/16).");
        }

        return network.PrefixLength == 0
            ? throw new InvalidOperationException(
                $"Setting '{KnownNetworksSetting}' has '{value}', which matches every address and would let any client forge its address and scheme.")
            : network;
    }
}
