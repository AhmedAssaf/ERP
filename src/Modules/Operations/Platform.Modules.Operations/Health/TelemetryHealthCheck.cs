using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// W-10 (O-14): the telemetry pipeline, so a dead pipeline is alerted by F-60 like Disk is. <c>GET</c> the collector's
/// <c>health_check</c> extension (healthy on 200) and Elasticsearch's <c>/_cluster/health</c> with the monitoring user's Basic
/// credentials (healthy on cluster status <c>green</c> or <c>yellow</c>), both at once within the job's per-check timeout.
/// Each node's disk is read at the same time (<c>_nodes/stats/fs</c>, W-10 follow-up 2026-10-02, fix round 1), and counts only
/// when the cluster answers green or yellow, so a red or unreadable cluster is reported alone: at the flood-stage watermark
/// Elasticsearch makes every index read-only while the cluster stays green, so the pipeline would drop every document under
/// a healthy check. The fullest node counts, in use = total minus available (as Elasticsearch computes its watermarks): Degraded at or above
/// <see cref="HighWatermarkPercent"/>, Unhealthy at or above <see cref="FloodStagePercent"/>, Elasticsearch's default
/// watermarks (on a disk large enough for <c>max_headroom</c> to apply, Elasticsearch blocks writes later than this, so
/// the check warns early, never late). The result names the part that failed (<c>collector</c>, <c>elasticsearch</c>,
/// <c>elasticsearch disk</c>) with the exception type, the HTTP status, the cluster status or the disk percentage only:
/// never a URL (it could carry credentials), the user name, the password, or a response body (N-10).
/// </summary>
internal sealed class TelemetryHealthCheck(HttpClient httpClient, TelemetryHealthSettings settings) : IHealthCheck
{
    private const string Collector = "collector";
    private const string Elasticsearch = "elasticsearch";
    private const string ElasticsearchDisk = "elasticsearch disk";

    /// <summary>Elasticsearch's default high disk watermark: no new shard is allocated on a node at or above it.</summary>
    internal const double HighWatermarkPercent = 90;

    /// <summary>Elasticsearch's default flood-stage watermark: every index with a shard on the node becomes read-only.</summary>
    internal const double FloodStagePercent = 95;

    private static readonly Finding Fine = new(HealthStatus.Healthy, null);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var findings = (await Task.WhenAll(ProbeCollectorAsync(cancellationToken), ProbeElasticsearchAsync(cancellationToken)))
            .Where(f => f.Text is not null)
            .ToList();
        if (findings.Count == 0)
        {
            return HealthCheckResult.Healthy();
        }

        var description = $"Telemetry pipeline: {string.Join("; ", findings.Select(f => f.Text))}.";
        return findings.Any(f => f.Status == HealthStatus.Unhealthy)
            ? HealthCheckResult.Unhealthy(description)
            : HealthCheckResult.Degraded(description);
    }

    private async Task<Finding> ProbeCollectorAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                settings.CollectorHealthUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode == HttpStatusCode.OK ? Fine : Failed($"{Collector} answered HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10): an HttpRequestException can carry the target URL.
            return Failed(Unreachable(Collector, ex));
        }
    }

    /// <summary>The cluster health and the disk at once (one timeout); the disk counts only when the cluster is green or yellow.</summary>
    private async Task<Finding> ProbeElasticsearchAsync(CancellationToken cancellationToken)
    {
        var cluster = ProbeClusterAsync(cancellationToken);
        var disk = ProbeDiskAsync(cancellationToken);
        await Task.WhenAll(cluster, disk);
        return await cluster is { } failure ? Failed(failure) : await disk;
    }

    private async Task<string?> ProbeClusterAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = Monitored(settings.ElasticsearchHealthUrl);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"{Elasticsearch} answered HTTP {(int)response.StatusCode}";
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var status = json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("status", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            // Only the three documented values are ever named; anything else in the body is never repeated.
            return status switch
            {
                "green" or "yellow" => null,
                "red" => $"{Elasticsearch} cluster status is red",
                _ => $"{Elasticsearch} cluster status is unknown",
            };
        }
        catch (JsonException ex)
        {
            return $"{Elasticsearch} answered an unreadable cluster health ({ex.GetType().Name})";
        }
        catch (Exception ex)
        {
            return Unreachable(Elasticsearch, ex);
        }
    }

    /// <summary>
    /// The fullest node's disk against the watermarks, from <c>nodes.*.fs.total</c>: in use is
    /// <c>total_in_bytes - available_in_bytes</c>. A node without positive numeric totals is skipped; an answer with no
    /// readable node at all is Unhealthy, since whether writes are blocked is then unknown. Only the percentage is ever
    /// repeated, never another part of the body.
    /// </summary>
    private async Task<Finding> ProbeDiskAsync(CancellationToken cancellationToken)
    {
        const string Unreadable = $"{ElasticsearchDisk} usage is unreadable";
        try
        {
            using var request = Monitored(settings.ElasticsearchNodesFsUrl);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Failed($"{ElasticsearchDisk} usage answered HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            double? fullest = null;
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Object)
            {
                foreach (var node in nodes.EnumerateObject())
                {
                    if (UsedPercent(node.Value) is { } percent)
                    {
                        fullest = Math.Max(fullest ?? 0, percent);
                    }
                }
            }

            return fullest switch
            {
                null => Failed(Unreadable),
                >= FloodStagePercent => Failed(
                    $"{ElasticsearchDisk} {Percent(fullest.Value)} used, at or above the flood stage ({Percent(FloodStagePercent)}): indices are read-only"),
                >= HighWatermarkPercent => new Finding(
                    HealthStatus.Degraded,
                    $"{ElasticsearchDisk} {Percent(fullest.Value)} used, at or above the high watermark ({Percent(HighWatermarkPercent)})"),
                _ => Fine,
            };
        }
        catch (JsonException ex)
        {
            return Failed($"{Unreadable} ({ex.GetType().Name})");
        }
        catch (Exception ex)
        {
            return Failed($"{ElasticsearchDisk} usage could not be read ({ex.GetType().Name})");
        }
    }

    /// <summary>The share of one node's disk in use, in percent, or null without readable totals.</summary>
    private static double? UsedPercent(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("fs", out var fs) || fs.ValueKind != JsonValueKind.Object
            || !fs.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Object
            || !total.TryGetProperty("total_in_bytes", out var size) || size.ValueKind != JsonValueKind.Number
            || !total.TryGetProperty("available_in_bytes", out var available) || available.ValueKind != JsonValueKind.Number
            || !size.TryGetInt64(out var sizeBytes) || !available.TryGetInt64(out var availableBytes)
            || sizeBytes <= 0 || availableBytes < 0 || availableBytes > sizeBytes)
        {
            return null;
        }

        return 100.0 * (sizeBytes - availableBytes) / sizeBytes;
    }

    /// <summary>A GET with the monitoring user's Basic credentials, when both are configured.</summary>
    private HttpRequestMessage Monitored(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(settings.ElasticsearchUser) && !string.IsNullOrEmpty(settings.ElasticsearchPassword))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{settings.ElasticsearchUser}:{settings.ElasticsearchPassword}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        return request;
    }

    private static string Percent(double value) => $"{value.ToString("0.#", CultureInfo.InvariantCulture)}%";

    private static Finding Failed(string text) => new(HealthStatus.Unhealthy, text);

    private static string Unreachable(string part, Exception exception) =>
        $"{part} could not be reached ({exception.GetType().Name})";

    /// <summary>What one probe found: Healthy with no text, or Degraded or Unhealthy with the text to report.</summary>
    private sealed record Finding(HealthStatus Status, string? Text);
}
