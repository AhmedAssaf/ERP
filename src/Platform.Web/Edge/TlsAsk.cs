using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Platform.Modules.Tenancy.Contracts;

namespace Platform.Web.Edge;

/// <summary>
/// The pilot's on-demand TLS (docs/19): Caddy calls <c>GET /internal/tls-ask?domain=&lt;host&gt;</c> before it obtains a
/// certificate for a host it has none for, and issues one only on a 200. Settings, section <c>TlsAsk</c>:
/// <list type="bullet">
/// <item><c>Port</c>: the listener that serves the endpoint and nothing else. It must be one of the ports Kestrel binds
/// (<c>Kestrel:Endpoints</c>, else <c>URLS</c>, else <c>HTTP_PORTS</c> and <c>HTTPS_PORTS</c>, as Kestrel chooses), and not
/// the only one. No Caddy site block proxies to it and Compose publishes it nowhere. Empty means no endpoint. The listener
/// is told apart by the connection's local port, never by the Host header, which the caller writes.</item>
/// <item><c>TenantBaseDomain</c>: required with <c>Port</c>; outside Development and Testing it needs at least two labels.
/// Only a host exactly one label under it can be allowed (the shape of <c>KeycloakAdmin:TenantUrl</c>,
/// <c>https://{slug}.&lt;base&gt;/</c>); custom domains are F-03.</item>
/// <item><c>ExcludedHosts</c>: hosts never allowed, beside the ones always excluded: <c>Platform:Host</c> and the hosts of
/// <c>Oidc:Authority</c> and <c>PlatformOidc:Authority</c> (Keycloak). Those have their own site blocks and certificates.</item>
/// <item><c>RequestsPerSecond</c> (default 20, burst the same): database lookups beyond it get a 429, which Caddy treats as a
/// refusal and asks again at the next handshake. Only a well-formed name under the base domain that the tenant directory has
/// not cached costs a token, so junk names (any SNI aimed at the address) never use up the rate.</item>
/// </list>
/// On every other listener any path under <c>/internal</c> is a 404 before tenant resolution, whatever the Host header.
/// </summary>
internal static class TlsAsk
{
    public const string Section = "TlsAsk";
    public const string PortSetting = Section + ":Port";
    public const string TenantBaseDomainSetting = Section + ":TenantBaseDomain";
    public const string ExcludedHostsSetting = Section + ":ExcludedHosts";
    public const string RequestsPerSecondSetting = Section + ":RequestsPerSecond";
    public static readonly PathString Path = "/internal/tls-ask";
    private static readonly PathString InternalPrefix = "/internal";
    private const int DefaultRequestsPerSecond = 20;

    public static IServiceCollection AddTlsAsk(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var settings = Read(configuration, environment);
        services.AddSingleton(settings);
        // A factory, not an instance: the container disposes only what it creates.
        services.AddSingleton(_ => new TlsAskRateLimit(settings.RequestsPerSecond));
        return services;
    }

    public static IApplicationBuilder UseTlsAsk(this IApplicationBuilder app) => app.UseMiddleware<TlsAskMiddleware>();

    public static bool IsInternalPath(PathString path) => path.StartsWithSegments(InternalPrefix, StringComparison.OrdinalIgnoreCase);

    private static TlsAskSettings Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var portText = configuration[PortSetting];
        if (string.IsNullOrWhiteSpace(portText))
        {
            return TlsAskSettings.Off;
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Setting '{PortSetting}' must be a TCP port (1 to 65535), not '{portText}'.");
        }

        var bound = BoundPorts(configuration);
        if (!bound.Contains(port) || bound.Count < 2)
        {
            throw new InvalidOperationException(
                $"Setting '{PortSetting}' ({port}) must be a port Kestrel binds beside the public one (for example "
                + $"ASPNETCORE_HTTP_PORTS=8080;{port}); bound now: {(bound.Count == 0 ? "none configured" : string.Join(", ", bound.Order()))}. "
                + "The ask listener serves nothing but the ask, so it can never be the app's only port.");
        }

        var baseDomain = TlsAskHosts.NormalizeBaseDomain(configuration[TenantBaseDomainSetting])
            ?? throw new InvalidOperationException(
                $"Setting '{TenantBaseDomainSetting}' must be a host name such as example.sa when '{PortSetting}' is set; "
                + "certificates are issued only for hosts one label under it.");
        if (!baseDomain.Contains('.', StringComparison.Ordinal) && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                $"Setting '{TenantBaseDomainSetting}' must have at least two labels (example.sa) outside Development, not '{baseDomain}'.");
        }

        var rateText = configuration[RequestsPerSecondSetting];
        var rate = DefaultRequestsPerSecond;
        if (!string.IsNullOrWhiteSpace(rateText)
            && (!int.TryParse(rateText, NumberStyles.None, CultureInfo.InvariantCulture, out rate) || rate < 1))
        {
            throw new InvalidOperationException($"Setting '{RequestsPerSecondSetting}' must be a whole number of at least 1, not '{rateText}'.");
        }

        return new TlsAskSettings(port, baseDomain, ExcludedHosts(configuration), rate);
    }

    /// <summary>The platform host, Keycloak's hosts from both authorities, and <c>TlsAsk:ExcludedHosts</c>.</summary>
    private static HashSet<string> ExcludedHosts(IConfiguration configuration)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        AddIfHost(excluded, configuration["Platform:Host"]);
        foreach (var authority in new[] { configuration["Oidc:Authority"], configuration["PlatformOidc:Authority"] })
        {
            if (Uri.TryCreate(authority, UriKind.Absolute, out var uri))
            {
                AddIfHost(excluded, uri.Host);
            }
        }

        foreach (var child in configuration.GetSection(ExcludedHostsSetting).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(child.Value))
            {
                continue;
            }

            var host = TlsAskHosts.NormalizeHost(child.Value.Trim())
                ?? throw new InvalidOperationException($"Setting '{child.Path}' must be a host name, not '{child.Value}'.");
            excluded.Add(host);
        }

        return excluded;
    }

    private static void AddIfHost(HashSet<string> excluded, string? value)
    {
        if (TlsAskHosts.NormalizeHost(value?.Trim()) is { } host)
        {
            excluded.Add(host);
        }
    }

    /// <summary>
    /// The ports Kestrel will listen on, by its own precedence: endpoints in <c>Kestrel:Endpoints</c>, else <c>URLS</c>,
    /// else <c>HTTP_PORTS</c> and <c>HTTPS_PORTS</c> (the <c>ASPNETCORE_</c> variables, which the web builder reads unprefixed).
    /// </summary>
    private static HashSet<int> BoundPorts(IConfiguration configuration)
    {
        var endpoints = configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]).ToList();
        if (endpoints.Count > 0)
        {
            return PortsOfUrls(endpoints);
        }

        var urls = configuration["URLS"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            return PortsOfUrls(urls.Split(';'));
        }

        var ports = new HashSet<int>();
        foreach (var key in new[] { "HTTP_PORTS", "HTTPS_PORTS" })
        {
            foreach (var text in (configuration[key] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                {
                    ports.Add(port);
                }
            }
        }

        return ports;
    }

    private static HashSet<int> PortsOfUrls(IEnumerable<string?> urls)
    {
        var ports = new HashSet<int>();
        foreach (var url in urls)
        {
            // Kestrel's wildcard hosts (+ and *) are not valid in a Uri.
            var text = url?.Trim().Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal);
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !uri.IsDefaultPort)
            {
                ports.Add(uri.Port);
            }
        }

        return ports;
    }
}

/// <summary>The parsed <c>TlsAsk</c> settings; <see cref="Port"/> null means the endpoint is off.</summary>
internal sealed record TlsAskSettings(int? Port, string? TenantBaseDomain, IReadOnlySet<string> ExcludedHosts, int RequestsPerSecond)
{
    public static TlsAskSettings Off { get; } = new(null, null, new HashSet<string>(), 1);
}

/// <summary>Host-name checks for the ask endpoint. Lower-case comparison throughout (RFC 4343).</summary>
internal static class TlsAskHosts
{
    private const int MaxHostLength = 253; // RFC 1035 section 2.3.4, textual form without the trailing dot.
    private const int MaxLabelLength = 63;

    /// <summary>
    /// The host to look up, or null when it cannot be one: not exactly one valid label under <paramref name="baseDomain"/>,
    /// an IP literal, or an excluded host. One trailing dot (the absolute form) is accepted.
    /// </summary>
    public static string? TenantHostOrNull(string? domain, string baseDomain, IReadOnlySet<string> excluded)
    {
        var host = NormalizeHost(domain);
        if (host is null)
        {
            return null;
        }

        var suffix = "." + baseDomain;
        if (!host.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var label = host[..^suffix.Length];
        return IsLabel(label) && !excluded.Contains(host) ? host : null;
    }

    /// <summary>The configured base domain, lower-cased, or null when it is not a plain host name.</summary>
    public static string? NormalizeBaseDomain(string? value) => NormalizeHost(value?.Trim());

    /// <summary>
    /// Lower-cased host without one trailing dot, or null unless every label is a valid LDH label and it is not an IP literal.
    /// </summary>
    public static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var host = value.EndsWith('.') ? value[..^1] : value;
        if (host.Length is 0 or > MaxHostLength)
        {
            return null;
        }

#pragma warning disable CA1308 // Host names compare in lower case (RFC 4343); tenancy.tenant_hosts stores them lower-cased.
        host = host.ToLowerInvariant();
#pragma warning restore CA1308
        return host.Split('.').All(IsLabel) && !IPAddress.TryParse(host, out _) ? host : null;
    }

    private static bool IsLabel(string label) =>
        label.Length is > 0 and <= MaxLabelLength
        && label[0] != '-'
        && label[^1] != '-'
        && label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}

/// <summary>
/// Serves <see cref="TlsAsk.Path"/> on the ask listener and nothing else there; refuses <c>/internal</c> on every other
/// listener. Registered before the platform host and tenant middleware, so neither runs for these requests.
/// </summary>
internal sealed partial class TlsAskMiddleware(
    RequestDelegate next, TlsAskSettings settings, TlsAskRateLimit limit, ILogger<TlsAskMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ITenantDirectory directory)
    {
        var onAskListener = settings.Port is { } port && context.Connection.LocalPort == port;
        if (!onAskListener)
        {
            if (TlsAsk.IsInternalPath(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) || !context.Request.Path.Equals(TlsAsk.Path, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var domains = context.Request.Query["domain"];
        if (domains.Count != 1)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Shape first, then the cache: neither costs a token, so only database lookups are rate-limited.
        var host = TlsAskHosts.TenantHostOrNull(domains[0], settings.TenantBaseDomain!, settings.ExcludedHosts);
        if (host is null)
        {
            LogRefused(logger, "(not a tenant host name)");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!directory.TryGetCached(host, out var tenant))
        {
            using var lease = limit.Limiter.AttemptAcquire();
            if (!lease.IsAcquired)
            {
                LogRateLimited(logger);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            tenant = await directory.FindByHostAsync(host, context.RequestAborted);
        }

        if (tenant is null)
        {
            LogRefused(logger, host);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        LogAllowed(logger, host);
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "On-demand TLS allowed for {Host}")]
    private static partial void LogAllowed(ILogger logger, string host);

    // Debug: anyone who can open a TLS handshake to the address causes one of these.
    [LoggerMessage(Level = LogLevel.Debug, Message = "On-demand TLS refused for {Host}")]
    private static partial void LogRefused(ILogger logger, string host);

    // Debug as well: a flood of handshakes would otherwise flood the logs.
    [LoggerMessage(Level = LogLevel.Debug, Message = "On-demand TLS ask refused by the rate limit")]
    private static partial void LogRateLimited(ILogger logger);
}

/// <summary>One token bucket for every ask request of the instance; the container disposes it.</summary>
internal sealed class TlsAskRateLimit(int requestsPerSecond) : IDisposable
{
    public RateLimiter Limiter { get; } = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = requestsPerSecond,
        TokensPerPeriod = requestsPerSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    });

    public void Dispose() => Limiter.Dispose();
}
