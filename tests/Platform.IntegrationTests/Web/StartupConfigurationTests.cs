using Microsoft.AspNetCore.Hosting;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// Outside Development and Testing the host refuses to start without its OIDC, platform host and Keycloak Admin API
/// settings (the Admin API invites staff, F-06).
/// </summary>
public class StartupConfigurationTests
{
    [Theory]
    [InlineData("Oidc:Authority")]
    [InlineData("Oidc:ClientSecret")]
    [InlineData("Platform:Host")]
    [InlineData("PlatformOidc:Authority")]
    [InlineData("PlatformOidc:ClientSecret")]
    [InlineData("KeycloakAdmin:BaseUrl")]
    [InlineData("KeycloakAdmin:ClientSecret")]
    [InlineData("KeycloakAdmin:TenantUrl")]
    public void Production_host_without_an_oidc_setting_does_not_start(string key)
    {
        using var factory = new PlatformWebFactory("Host=unused;Database=unused")
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("KeycloakAdmin:BaseUrl", "https://keycloak.invalid");
                builder.UseSetting("KeycloakAdmin:ClientSecret", "unused-in-tests");
                builder.UseSetting("KeycloakAdmin:TenantUrl", "https://{slug}.example.invalid/");
                builder.UseSetting(key, string.Empty);
            });

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain(key);
    }
}
