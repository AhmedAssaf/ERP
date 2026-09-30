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
/// (<see cref="ForwardedHeadersOptions.ForwardLimit"/> 1) only the last <c>X-Forwarded-For</c> entry is taken. Caddy v2
/// replaces the header with the address of the client it sees (it does not trust incoming <c>X-Forwarded-*</c> unless
/// told to), and the one-hop limit keeps an address a client wrote itself from being believed even if that changes.
/// Outside Testing the host refuses to start without either setting; it also refuses a network written with host bits
/// set, and networks that together match every IPv4 or every IPv6 address (<c>/0</c>, two <c>/1</c>, ...).</item>
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
        RefuseNetworksCoveringEverything(networks);
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
        // Host bits first: depending on the runtime, IPNetwork either refuses 172.18.0.5/16 or quietly accepts it, and
        // either way that is the usual slip for "the proxy's address".
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0
            && IPAddress.TryParse(value[..slash], out var address)
            && int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix)
            && prefix <= address.GetAddressBytes().Length * 8)
        {
            var bytes = address.GetAddressBytes();
            for (var bit = prefix; bit < bytes.Length * 8; bit++)
            {
                bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
            }

            var network = new IPAddress(bytes);
            if (!network.Equals(address))
            {
                throw new InvalidOperationException(
                    $"Setting '{KnownNetworksSetting}' has '{value}', which has host bits set. Write the network as {network}/{prefix}, "
                    + $"or name the single proxy in '{KnownProxiesSetting}' as {address}.");
            }
        }

        return IPNetwork.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Setting '{KnownNetworksSetting}' has '{value}', which is not a network in CIDR form (for example 172.18.0.0/16).");
    }

    /// <summary>Refuses networks whose union is every address of a family: they would let any client forge its address and scheme.</summary>
    private static void RefuseNetworksCoveringEverything(List<IPNetwork> networks)
    {
        foreach (var family in networks.GroupBy(n => n.BaseAddress.AddressFamily))
        {
            var bits = family.First().BaseAddress.GetAddressBytes().Length * 8;
            var ranges = family
                .Select(n => (Start: ToNumber(n.BaseAddress), Size: n.PrefixLength == 0 ? UInt128.Zero : UInt128.One << (bits - n.PrefixLength)))
                .OrderBy(r => r.Start)
                .ToList();
            // Covered so far: [0, next). Size 0 stands for a /0; an IPv6 range that ends at the top wraps to 0.
            UInt128 next = 0;
            var everything = false;
            foreach (var (start, size) in ranges)
            {
                if (start > next)
                {
                    break;
                }

                if (size == 0 || start + size == 0 || (bits == 32 && start + size > uint.MaxValue))
                {
                    everything = true;
                    break;
                }

                next = UInt128.Max(next, start + size);
            }

            if (everything)
            {
                throw new InvalidOperationException(
                    $"Setting '{KnownNetworksSetting}' matches every {(bits == 32 ? "IPv4" : "IPv6")} address, which would let any client forge "
                    + $"its address and scheme. Name Caddy's address in '{KnownProxiesSetting}' instead.");
            }
        }
    }

    private static UInt128 ToNumber(IPAddress address)
    {
        UInt128 number = 0;
        foreach (var b in address.GetAddressBytes())
        {
            number = (number << 8) | b;
        }

        return number;
    }
}
