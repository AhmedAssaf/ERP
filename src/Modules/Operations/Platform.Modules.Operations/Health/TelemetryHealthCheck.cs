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
/// An unhealthy result names the part that failed (<c>collector</c>, <c>elasticsearch</c>, or both) with the exception type,
/// the HTTP status or the cluster status only: never a URL (it could carry credentials), the user name, the password, or a
/// response body (N-10).
/// </summary>
internal sealed class TelemetryHealthCheck(HttpClient httpClient, TelemetryHealthSettings settings) : IHealthCheck
{
    private const string Collector = "collector";
    private const string Elasticsearch = "elasticsearch";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var failures = await Task.WhenAll(ProbeCollectorAsync(cancellationToken), ProbeElasticsearchAsync(cancellationToken));
        var failed = failures.OfType<string>().ToList();
        return failed.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Telemetry pipeline: {string.Join("; ", failed)}.");
    }

    private async Task<string?> ProbeCollectorAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                settings.CollectorHealthUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode == HttpStatusCode.OK ? null : $"{Collector} answered HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10): an HttpRequestException can carry the target URL.
            return Unreachable(Collector, ex);
        }
    }

    private async Task<string?> ProbeElasticsearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.ElasticsearchHealthUrl);
            if (!string.IsNullOrEmpty(settings.ElasticsearchUser) && !string.IsNullOrEmpty(settings.ElasticsearchPassword))
            {
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{settings.ElasticsearchUser}:{settings.ElasticsearchPassword}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }

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

    private static string Unreachable(string part, Exception exception) =>
        $"{part} could not be reached ({exception.GetType().Name})";
}
