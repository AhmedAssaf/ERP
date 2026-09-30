using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Platform.IntegrationTests.Infrastructure;

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
                .UseSetting(TestSecrets.CrAuditKeySetting.Key, TestSecrets.CrAuditKeySetting.Value));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Unhealthy");
    }
}
