using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>Spec 3.2: <c>GET /health/ready</c> on the management port, expecting <c>{"status":"UP"}</c>.</summary>
internal sealed class KeycloakHealthCheck(HttpClient httpClient, string managementUrl) : IHealthCheck
{
    private const string Component = "Keycloak";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var baseUri = new Uri(managementUrl.EndsWith('/') ? managementUrl : managementUrl + "/");
            using var response = await httpClient.GetAsync(new Uri(baseUri, "health/ready"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Unhealthy($"{Component} reported not ready.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var status = json.RootElement.TryGetProperty("status", out var value) ? value.GetString() : null;
            return status == "UP"
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"{Component} reported not ready.");
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10): an HttpRequestException can carry the target URL.
            return HealthCheckResult.Unhealthy(
                HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach(Component)));
        }
    }
}
