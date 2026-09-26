using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>Spec 3.2: the web host's own <c>/health</c>, probed from the worker.</summary>
internal sealed class WebHealthCheck(HttpClient httpClient, string webHealthUrl) : IHealthCheck
{
    private const string Component = "Web";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(webHealthUrl, cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"The {Component} host reported unhealthy.");
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10): an HttpRequestException can carry the target URL.
            return HealthCheckResult.Unhealthy(
                HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach($"the {Component} host")));
        }
    }
}
