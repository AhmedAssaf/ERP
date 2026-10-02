using System.Collections.Frozen;

namespace Platform.Web.Usage;

/// <summary>
/// The console usage page's link to the Kibana dashboard "WaslaBid usage" (saved object id <c>waslabid-usage</c>, spec 6.7), from
/// <see cref="Setting"/>. Only an absolute <c>http</c> or <c>https</c> address makes a link; anything else (a relative
/// path, <c>javascript:</c>, <c>data:</c>, <c>file:</c>, a host without a scheme) makes none, and one warning per process
/// names the scheme only: a well-known one by name, any other as "other", a relative value as "relative", never the value.
/// Read once: the setting is deployment configuration, not something that changes while the host runs.
/// </summary>
internal sealed partial class KibanaLink(IConfiguration configuration, ILogger<KibanaLink> logger)
{
    /// <summary>The setting that links the page to the dashboard.</summary>
    public const string Setting = "Observability:KibanaUrl";

    private const string DashboardPath = "app/dashboards#/view/waslabid-usage";

    // Schemes a misconfiguration plausibly carries; any other scheme could be part of a host name ("kibana.local:5601"
    // parses as the scheme "kibana.local") and is logged as "other".
    private static readonly FrozenSet<string> KnownSchemes = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase, "javascript", "vbscript", "data", "blob", "file", "ftp", "ftps", "sftp", "mailto", "ws", "wss");

    private readonly Lazy<string?> _dashboardUrl = new(() => Resolve(configuration[Setting], logger));

    /// <summary>The dashboard's absolute address, or null when the setting is absent or not an absolute http(s) address.</summary>
    public string? DashboardUrl => _dashboardUrl.Value;

    private static string? Resolve(string? value, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        // On Linux "/path" parses as an absolute file URI; only a scheme written in the value counts as one.
        var uri = Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) && trimmed.StartsWith(parsed.Scheme + ":", StringComparison.OrdinalIgnoreCase)
            ? parsed
            : null;
        if (uri is not null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(uri.Host))
        {
            return $"{trimmed.TrimEnd('/')}/{DashboardPath}";
        }

        var scheme = uri is null ? "relative" : KnownSchemes.Contains(uri.Scheme) ? uri.Scheme.ToLowerInvariant() : "other";
        NotAnHttpAddress(logger, Setting, scheme);
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Setting} is not an absolute http or https address (scheme {Scheme}); the usage page shows no Kibana link.")]
    private static partial void NotAnHttpAddress(ILogger logger, string setting, string scheme);
}
