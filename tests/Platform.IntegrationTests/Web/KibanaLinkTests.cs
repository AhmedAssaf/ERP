using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Web.Usage;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-10 follow-up (final review, task 7b; closed 2026-10-03): the dashboard link is built on the configured address's parts,
/// not by appending text: the dashboard's path goes after the configured path (a Kibana space is a path prefix,
/// <c>/s/{id}</c>), a configured fragment gives way to the dashboard's own, and a configured query string and user
/// information (<c>user:password@</c>) never reach the page (N-10): Kibana reads nothing it needs from a query (its global
/// state <c>_g</c> lives inside the hash route), while a proxy in front of it may take a token there, and Kibana asks for its
/// own login. No host is started.
/// </summary>
public sealed class KibanaLinkTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5601", "http://127.0.0.1:5601/app/dashboards#/view/waslabid-usage")]
    [InlineData("http://127.0.0.1:5601/", "http://127.0.0.1:5601/app/dashboards#/view/waslabid-usage")]
    [InlineData("https://kibana.example/base/", "https://kibana.example/base/app/dashboards#/view/waslabid-usage")]
    [InlineData("https://kibana.example/s/ops", "https://kibana.example/s/ops/app/dashboards#/view/waslabid-usage")]
    [InlineData("https://kibana.example/base#/home", "https://kibana.example/base/app/dashboards#/view/waslabid-usage")]
    [InlineData("https://kibana.example#/home", "https://kibana.example/app/dashboards#/view/waslabid-usage")]
    [InlineData("https://kibana.example:8443/base/", "https://kibana.example:8443/base/app/dashboards#/view/waslabid-usage")]
    public void The_dashboard_path_goes_after_the_configured_path_and_its_fragment_replaces_a_configured_one(string setting, string expected) =>
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

    [Theory]
    [InlineData("https://kibana.example/base?token=kibana-secret-71f0", "https://kibana.example/base/app/dashboards#/view/waslabid-usage")]
    [InlineData("https://kibana.example/s/ops?token=kibana-secret-71f0#/home", "https://kibana.example/s/ops/app/dashboards#/view/waslabid-usage")]
    [InlineData("https://kibana.example:8443/base/?x=1&token=kibana-secret-71f0", "https://kibana.example:8443/base/app/dashboards#/view/waslabid-usage")]
    public void A_query_in_the_setting_never_reaches_the_link(string setting, string expected)
    {
        var link = Link(setting);

        link.ShouldBe(expected);
        link!.ShouldNotContain("kibana-secret");
        link!.ShouldNotContain("?");
    }

    private static string? Link(string setting)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [KibanaLink.Setting] = setting })
            .Build();
        return new KibanaLink(configuration, NullLogger<KibanaLink>.Instance).DashboardUrl;
    }
}
