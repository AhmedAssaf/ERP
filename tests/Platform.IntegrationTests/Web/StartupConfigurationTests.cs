using Microsoft.AspNetCore.Hosting;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Web;

/// <summary>Outside Development and Testing the host refuses to start without its OIDC settings.</summary>
public class StartupConfigurationTests
{
    [Theory]
    [InlineData("Oidc:Authority")]
    [InlineData("Oidc:ClientSecret")]
    public void Production_host_without_an_oidc_setting_does_not_start(string key)
    {
        using var factory = new PlatformWebFactory("Host=unused;Database=unused")
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting(key, string.Empty);
            });

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain(key);
    }
}
