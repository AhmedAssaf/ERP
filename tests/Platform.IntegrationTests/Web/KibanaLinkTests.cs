using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Web.Usage;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-10 follow-up (final review, task 7b; closed 2026-10-03): the dashboard link is built on the configured address's parts,
/// not by appending text, so a configured query string (a Kibana space or global state) stays a query string after the
/// dashboard's path, and a configured fragment gives way to the dashboard's own. User information in the setting
/// (<c>user:password@</c>) never reaches the page (N-10): Kibana asks for its own login. No host is started.
/// </summary>
public sealed class KibanaLinkTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5601", "http://127.0.0.1:5601/app/dashboards#/view/waslabid-usage")]
    [InlineData("http://127.0.0.1:5601/", "http://127.0.0.1:5601/app/dashboards#/view/waslabid-usage")]
    [InlineData("https://kibana.example/base/", "https://kibana.example/base/app/dashboards#/view/waslabid-usage")]
    [InlineData("https://kibana.example/s/ops?_g=(time:(from:now-7d))", "https://kibana.example/s/ops/app/dashboards?_g=(time:(from:now-7d))#/view/waslabid-usage")]
    [InlineData("https://kibana.example/base?space=ops#/home", "https://kibana.example/base/app/dashboards?space=ops#/view/waslabid-usage")]
    [InlineData("https://kibana.example#/home", "https://kibana.example/app/dashboards#/view/waslabid-usage")]
    [InlineData("https://kibana.example:8443/base/?x=1", "https://kibana.example:8443/base/app/dashboards?x=1#/view/waslabid-usage")]
    public void The_dashboard_path_goes_after_the_configured_path_and_before_its_query(string setting, string expected) =>
        Link(setting).ShouldBe(expected);

    [Theory]
    [InlineData("https://ops:kibana-secret@kibana.example/base")]
    [InlineData("https://ops@kibana.example/base")]
    public void User_information_in_the_setting_never_reaches_the_link(string setting)
    {
        var link = Link(setting);

        link.ShouldBe("https://kibana.example/base/app/dashboards#/view/waslabid-usage");
        link!.ShouldNotContain("@");
    }

    private static string? Link(string setting)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [KibanaLink.Setting] = setting })
            .Build();
        return new KibanaLink(configuration, NullLogger<KibanaLink>.Instance).DashboardUrl;
    }
}
