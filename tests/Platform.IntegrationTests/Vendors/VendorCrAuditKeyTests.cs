using Microsoft.AspNetCore.Hosting;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// V-6: the duplicate-CR audit is keyed with <c>Vendors:CrAuditKey</c> (base64, at least 32 bytes). The web host refuses
/// to start without a usable key, in Development too, and names the setting without echoing its value (N-10).
/// </summary>
public sealed class VendorCrAuditKeyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all!")]
    [InlineData("c2hvcnQta2V5LW9ubHktMjQtYnl0ZXMtLQ==")]
    public void The_web_host_does_not_start_without_a_usable_cr_audit_key(string key)
    {
        using var factory = new PlatformWebFactory("Host=unused;Database=unused", environment: "Development")
            .WithWebHostBuilder(builder => builder.UseSetting("Vendors:CrAuditKey", key));

        var refused = Should.Throw<Exception>(() => factory.Server);

        var message = Messages(refused);
        message.ShouldContain("Vendors:CrAuditKey");
        if (key.Length > 0)
        {
            message.ShouldNotContain(key);
        }
    }

    private static string Messages(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }
}
