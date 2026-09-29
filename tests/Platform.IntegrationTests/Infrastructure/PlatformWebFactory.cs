using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// Keycloak settings for a host that keeps its real cookie and OpenID Connect handlers. The platform realm settings are
/// optional; without them the platform scheme points at an address that is never contacted.
/// </summary>
public sealed record OidcSettings(string Authority, string ClientSecret, string? PlatformAuthority = null, string? PlatformClientSecret = null);

/// <summary>
/// The web host, in the Testing environment unless another is given. By default a header-driven test scheme replaces
/// cookie and OIDC sign-in; pass <see cref="OidcSettings"/> to keep the real handlers against a Keycloak instance instead.
/// </summary>
internal sealed class PlatformWebFactory(string appConnectionString, OidcSettings? oidc = null, string environment = "Testing")
    : WebApplicationFactory<Program>
{
    /// <summary>The platform console's host name in tests, as <c>Platform:Host</c> sets it in Development.</summary>
    public const string PlatformHost = "platform.localhost";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Platform", appConnectionString);
        // W-24: the key ring's own role on the same database.
        builder.UseSetting("ConnectionStrings:KeyRing", TestSecrets.KeyRingConnectionString(appConnectionString));
        builder.UseSetting("Oidc:Authority", oidc?.Authority ?? "https://keycloak.invalid/realms/waslabid");
        builder.UseSetting("Oidc:ClientSecret", oidc?.ClientSecret ?? "unused-in-tests");
        builder.UseSetting("Platform:Host", PlatformHost);
        builder.UseSetting("PlatformOidc:Authority", oidc?.PlatformAuthority ?? "https://keycloak.invalid/realms/waslabid-platform");
        builder.UseSetting("PlatformOidc:ClientSecret", oidc?.PlatformClientSecret ?? "unused-in-tests");
        builder.UseSetting(TestSecrets.CrAuditKeySetting.Key, TestSecrets.CrAuditKeySetting.Value);
        if (environment is not ("Testing" or "Development"))
        {
            // Required outside Development and Testing; nothing in such a test contacts the Keycloak Admin API.
            builder.UseSetting("KeycloakAdmin:BaseUrl", "https://keycloak.invalid");
            builder.UseSetting("KeycloakAdmin:ClientSecret", "unused-in-tests");
            builder.UseSetting("KeycloakAdmin:TenantUrl", "https://{slug}.example.invalid/");
            // W-24: required outside Development and Testing as well: the edge proxy's address and the key ring certificate.
            builder.UseSetting("ForwardedHeaders:KnownProxies:0", "10.0.0.1");
            builder.UseSetting("DataProtection:CertificatePath", TestCertificates.KeyRingPath);
            builder.UseSetting("DataProtection:CertificatePassword", TestCertificates.KeyRingPassword);
        }

        if (oidc is not null)
        {
            builder.UseSetting("Oidc:RequireHttpsMetadata", "false");
            builder.UseSetting("PlatformOidc:RequireHttpsMetadata", "false");
            return;
        }

        builder.ConfigureTestServices(services => services.AddTestAuthentication());
    }

    public HttpClient ClientFor(string host) =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });
}
