using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Platform.Modules.Tenancy.Contracts;
using Platform.Web.PlatformHost;

namespace Platform.Web.Edge;

/// <summary>
/// The pilot's on-demand TLS (docs/19): Caddy calls <c>GET /internal/tls-ask?domain=&lt;host&gt;</c> before it obtains a
/// certificate for a host it has none for, and issues one only on a 200. Settings, section <c>TlsAsk</c>:
/// <list type="bullet">
/// <item><c>Port</c>: the listener that serves the endpoint and nothing else. Kestrel must also bind it
/// (<c>ASPNETCORE_HTTP_PORTS=8080;8081</c>); no Caddy site block proxies to it and Compose publishes it nowhere, so only
/// containers on the edge network reach it. Empty means no endpoint. The listener is told apart by the connection's local
/// port, never by the Host header, which the caller writes.</item>
/// <item><c>TenantBaseDomain</c>: required with <c>Port</c>. Only a host exactly one label under it can be allowed (the
/// shape of <c>KeycloakAdmin:TenantUrl</c>, <c>https://{slug}.&lt;base&gt;/</c>); custom domains are F-03.</item>
/// <item><c>RequestsPerSecond</c> (default 20, burst the same): lookups beyond it get a 429, which Caddy treats as a refusal
/// and asks again at the next handshake. The tenant directory caches each answer as well (60 s found, 5 s missing).</item>
/// </list>
/// On every other listener any path under <c>/internal</c> is a 404 before tenant resolution, whatever the Host header.
/// </summary>
internal static class TlsAsk
{
    public const string Section = "TlsAsk";
    public const string PortSetting = Section + ":Port";
    public const string TenantBaseDomainSetting = Section + ":TenantBaseDomain";
    public const string RequestsPerSecondSetting = Section + ":RequestsPerSecond";
    public static readonly PathString Path = "/internal/tls-ask";
    private static readonly PathString InternalPrefix = "/internal";
    private const int DefaultRequestsPerSecond = 20;

    public static IServiceCollection AddTlsAsk(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = Read(configuration);
        services.AddSingleton(settings);
        // A factory, not an instance: the container disposes only what it creates.
        services.AddSingleton(_ => new TlsAskRateLimit(settings.RequestsPerSecond));
        return services;
    }

    public static IApplicationBuilder UseTlsAsk(this IApplicationBuilder app) => app.UseMiddleware<TlsAskMiddleware>();

    public static bool IsInternalPath(PathString path) => path.StartsWithSegments(InternalPrefix, StringComparison.OrdinalIgnoreCase);

    private static TlsAskSettings Read(IConfiguration configuration)
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

        var baseDomain = TlsAskHosts.NormalizeBaseDomain(configuration[TenantBaseDomainSetting])
            ?? throw new InvalidOperationException(
                $"Setting '{TenantBaseDomainSetting}' must be a host name such as example.sa when '{PortSetting}' is set; "
                + "certificates are issued only for hosts one label under it.");

        var rateText = configuration[RequestsPerSecondSetting];
        var rate = DefaultRequestsPerSecond;
        if (!string.IsNullOrWhiteSpace(rateText)
            && (!int.TryParse(rateText, NumberStyles.None, CultureInfo.InvariantCulture, out rate) || rate < 1))
        {
            throw new InvalidOperationException($"Setting '{RequestsPerSecondSetting}' must be a whole number of at least 1, not '{rateText}'.");
        }

        return new TlsAskSettings(port, baseDomain, rate);
    }
}

/// <summary>The parsed <c>TlsAsk</c> settings; <see cref="Port"/> null means the endpoint is off.</summary>
internal sealed record TlsAskSettings(int? Port, string? TenantBaseDomain, int RequestsPerSecond)
{
    public static TlsAskSettings Off { get; } = new(null, null, 1);
}

/// <summary>Host-name checks for the ask endpoint. Lower-case comparison throughout (RFC 4343).</summary>
internal static class TlsAskHosts
{
    private const int MaxHostLength = 253; // RFC 1035 section 2.3.4, textual form without the trailing dot.
    private const int MaxLabelLength = 63;

    /// <summary>
    /// The host to look up, or null when it cannot be one: not exactly one valid label under <paramref name="baseDomain"/>,
    /// an IP literal, or the platform host. One trailing dot (the absolute form) is accepted.
    /// </summary>
    public static string? TenantHostOrNull(string? domain, string baseDomain, string? platformHost)
    {
        var host = Normalize(domain);
        if (host is null || IPAddress.TryParse(host, out _))
        {
            return null;
        }

        var suffix = "." + baseDomain;
        if (!host.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var label = host[..^suffix.Length];
        if (!IsLabel(label))
        {
            return null;
        }

        return platformHost is not null && string.Equals(host, Normalize(platformHost), StringComparison.Ordinal) ? null : host;
    }

    /// <summary>The configured base domain, lower-cased, or null when it is not a plain host name.</summary>
    public static string? NormalizeBaseDomain(string? value)
    {
        var host = Normalize(value?.Trim());
        return host is null || IPAddress.TryParse(host, out _) ? null : host;
    }

    /// <summary>Lower-cased host without one trailing dot, or null unless every label is a valid LDH label.</summary>
    private static string? Normalize(string? value)
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
        return host.Split('.').All(IsLabel) ? host : null;
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
    RequestDelegate next, TlsAskSettings settings, TlsAskRateLimit limit, IOptions<PlatformHostOptions> platform, ILogger<TlsAskMiddleware> logger)
{
    private readonly string? _platformHost = string.IsNullOrWhiteSpace(platform.Value.Host) ? null : platform.Value.Host.Trim();

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

        using var lease = limit.Limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            LogRateLimited(logger);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var domains = context.Request.Query["domain"];
        if (domains.Count != 1)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var host = TlsAskHosts.TenantHostOrNull(domains[0], settings.TenantBaseDomain!, _platformHost);
        var allowed = host is not null && await directory.FindByHostAsync(host, context.RequestAborted) is not null;
        if (allowed)
        {
            LogAllowed(logger, host!);
        }
        else
        {
            LogRefused(logger, host ?? "(not a tenant host name)");
        }

        context.Response.StatusCode = allowed ? StatusCodes.Status200OK : StatusCodes.Status404NotFound;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "On-demand TLS allowed for {Host}")]
    private static partial void LogAllowed(ILogger logger, string host);

    // Debug: anyone who can open a TLS handshake under the base domain causes one of these.
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
