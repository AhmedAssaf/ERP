using Microsoft.Extensions.Configuration;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// Configuration of the Telemetry check (W-10, O-14): the OpenTelemetry Collector's <c>health_check</c> extension and
/// Elasticsearch's <c>/_cluster/health</c> and, beside it, <c>/_cat/allocation</c> (the disk use per node; W-10 follow-up),
/// both read with the monitoring user (cluster privilege <c>monitor</c> only). The
/// password comes from user secrets on a developer machine and the secret store on the pilot, never from an appsettings
/// file (N-10). A class rather than a record, so no generated <c>ToString</c> ever prints the password.
/// </summary>
internal sealed class TelemetryHealthSettings
{
    public const string CollectorHealthUrlSetting = "Telemetry:CollectorHealthUrl";
    public const string ElasticsearchHealthUrlSetting = "Telemetry:ElasticsearchHealthUrl";
    public const string ElasticsearchUserSetting = "Telemetry:ElasticsearchUser";
    public const string ElasticsearchPasswordSetting = "Telemetry:ElasticsearchPassword";

    public TelemetryHealthSettings(Uri collectorHealthUrl, Uri elasticsearchHealthUrl, string? elasticsearchUser, string? elasticsearchPassword)
    {
        ArgumentNullException.ThrowIfNull(collectorHealthUrl);
        ArgumentNullException.ThrowIfNull(elasticsearchHealthUrl);
        CollectorHealthUrl = collectorHealthUrl;
        ElasticsearchHealthUrl = elasticsearchHealthUrl;
        ElasticsearchAllocationUrl = AllocationUrl(elasticsearchHealthUrl);
        ElasticsearchUser = elasticsearchUser;
        ElasticsearchPassword = elasticsearchPassword;
    }

    public Uri CollectorHealthUrl { get; }

    public Uri ElasticsearchHealthUrl { get; }

    /// <summary>
    /// <c>_cat/allocation</c> beside the cluster health URL (the same scheme, host, port and any path prefix before
    /// <c>/_cluster/health</c>), as JSON with only the <c>disk.percent</c> column.
    /// </summary>
    public Uri ElasticsearchAllocationUrl { get; }

    public string? ElasticsearchUser { get; }

    public string? ElasticsearchPassword { get; }

    private static Uri AllocationUrl(Uri healthUrl)
    {
        const string Health = "/_cluster/health";
        var path = healthUrl.AbsolutePath.TrimEnd('/');
        var prefix = path.EndsWith(Health, StringComparison.OrdinalIgnoreCase) ? path[..^Health.Length] : string.Empty;
        return new UriBuilder(healthUrl) { Path = prefix + "/_cat/allocation", Query = "format=json&h=disk.percent", Fragment = string.Empty }.Uri;
    }

    /// <summary>
    /// The settings when both health URLs are configured; null otherwise, and the host then has no Telemetry check (a host
    /// without the telemetry stack, such as CI, must not alert on it). A URL that is not an absolute http or https URL stops
    /// the host, naming the setting and never the value, which could carry credentials.
    /// </summary>
    public static TelemetryHealthSettings? FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var collector = configuration[CollectorHealthUrlSetting];
        var elasticsearch = configuration[ElasticsearchHealthUrlSetting];
        if (string.IsNullOrWhiteSpace(collector) || string.IsNullOrWhiteSpace(elasticsearch))
        {
            return null;
        }

        return new TelemetryHealthSettings(
            HttpUrl(collector, CollectorHealthUrlSetting),
            HttpUrl(elasticsearch, ElasticsearchHealthUrlSetting),
            configuration[ElasticsearchUserSetting],
            configuration[ElasticsearchPasswordSetting]);
    }

    private static Uri HttpUrl(string value, string setting) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? url
            : throw new InvalidOperationException($"Setting '{setting}' is not an absolute http or https URL.");
}
