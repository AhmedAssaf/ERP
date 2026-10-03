using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Platform.IntegrationTests.Infrastructure;
using Platform.Shared.Telemetry;

namespace Platform.IntegrationTests.Web;

public class HealthEndpointTests
{
    /// <remarks>
    /// Since W-24 /health includes the key ring (in the database): without a database it still answers, with no tenant and
    /// no sign-in, but Unhealthy, since every page would fail. The healthy answer is in
    /// <c>DataProtectionKeyRingTests.With_a_readable_key_ring_health_is_healthy</c>.
    /// </remarks>
    [Fact]
    public async Task Health_endpoint_answers_without_a_tenant_or_sign_in_and_is_unhealthy_without_a_database()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b
                .UseSetting("ConnectionStrings:Platform", "Host=unused;Database=unused")
                .UseSetting("ConnectionStrings:KeyRing", TestSecrets.KeyRingConnectionString("Host=unused;Database=unused"))
                .UseSetting(TestSecrets.CrAuditKeySetting.Key, TestSecrets.CrAuditKeySetting.Value)
                .UseSetting(TestSecrets.JobSigningKeySetting.Key, TestSecrets.JobSigningKeySetting.Value)
                // W-10: never export to a developer's collector from a test host.
                .UseSetting(TelemetryModule.OtlpEndpointSetting, string.Empty));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Unhealthy");
    }

    /// <remarks>
    /// W-10 (O-15): liveness, for the pilot's container health checks. No check runs, so the same unusable database as the
    /// test above still gives Healthy: the process answers, which is all <c>/alive</c> claims.
    /// </remarks>
    [Fact]
    public async Task Alive_answers_healthy_without_a_database_tenant_or_sign_in()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b
                .UseSetting("ConnectionStrings:Platform", "Host=unused;Database=unused")
                .UseSetting("ConnectionStrings:KeyRing", TestSecrets.KeyRingConnectionString("Host=unused;Database=unused"))
                .UseSetting(TestSecrets.CrAuditKeySetting.Key, TestSecrets.CrAuditKeySetting.Value)
                .UseSetting(TestSecrets.JobSigningKeySetting.Key, TestSecrets.JobSigningKeySetting.Value)
                .UseSetting(TelemetryModule.OtlpEndpointSetting, string.Empty));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/alive", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }
}
