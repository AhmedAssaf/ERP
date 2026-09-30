using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Web.Usage;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-10 console usage page (spec 6.3, 6.6), QA additions beside the usage page tests: the per-instance caveat of "Online
/// now" in both languages, and platform admins, who are the page's readers, never counted among the users online.
/// </summary>
public sealed partial class PlatformConsoleTests
{
    [Theory]
    [InlineData("en-US", "Online now counts the sessions connected to this web server.")]
    [InlineData("ar-SA", "يُحتسب المتصلون الآن من الجلسات المتصلة بخادم الويب هذا.")]
    public async Task The_usage_page_says_that_online_now_counts_this_web_server_only(string culture, string caveat)
    {
        await using var factory = Factory();

        var html = await ConsolePageInAsync(factory, UsagePath, culture);

        html.ShouldContain(caveat);
    }

    [Fact]
    public async Task Platform_admins_online_are_not_counted_on_the_usage_page()
    {
        await StoreUsageAsync(DateTimeOffset.UtcNow, []);
        await using var factory = Factory();
        var circuits = factory.Services.GetRequiredService<ConnectedCircuits>();
        circuits.Add(new object(), null, UsageKind.Platform, "platform-reader", () => false);
        circuits.Add(new object(), "acme", UsageKind.Staff, "staff-online", () => false);

        var html = await GetPageAsync(factory, UsagePath, PlatformAdmin());

        TileValue(html, "now").ShouldBe("1", "only the acme staff member; the platform admin is the reader");
        Element(html, "article", "data-tile", "now").ShouldContain("data-value=\"0\"", Case.Sensitive, "no vendor online either");
        Row(html, "data-tenant", "acme").ShouldContain(UsersCell("now", "staff", "1"));
    }
}
