using Microsoft.Extensions.Configuration;
using Platform.Modules.Operations.Health;

namespace Platform.UnitTests.Operations;

/// <summary>
/// W-10 (O-14): the Telemetry check exists only where both health URLs are configured, so a host without the telemetry
/// stack (CI) does not alert on it. A malformed URL stops the host, naming the setting and never the value (N-10).
/// </summary>
public sealed class TelemetryHealthSettingsTests
{
    private const string Collector = "http://localhost:13133/";
    private const string Elasticsearch = "http://localhost:9200/_cluster/health";

    [Fact]
    public void Both_urls_configure_the_check_with_the_monitoring_credentials()
    {
        var settings = TelemetryHealthSettings.FromConfiguration(Configuration(new()
        {
            [TelemetryHealthSettings.CollectorHealthUrlSetting] = Collector,
            [TelemetryHealthSettings.ElasticsearchHealthUrlSetting] = Elasticsearch,
            [TelemetryHealthSettings.ElasticsearchUserSetting] = "waslabid_monitor",
            [TelemetryHealthSettings.ElasticsearchPasswordSetting] = "not-a-real-password",
        }));

        settings.ShouldNotBeNull();
        settings.CollectorHealthUrl.ShouldBe(new Uri(Collector));
        settings.ElasticsearchHealthUrl.ShouldBe(new Uri(Elasticsearch));
        settings.ElasticsearchUser.ShouldBe("waslabid_monitor");
        settings.ElasticsearchPassword.ShouldBe("not-a-real-password");
    }

    [Theory]
    [InlineData(null, Elasticsearch)]
    [InlineData(Collector, null)]
    [InlineData("", Elasticsearch)]
    [InlineData(Collector, " ")]
    [InlineData(null, null)]
    public void Without_both_urls_there_is_no_telemetry_check(string? collector, string? elasticsearch) =>
        TelemetryHealthSettings.FromConfiguration(Configuration(new()
        {
            [TelemetryHealthSettings.CollectorHealthUrlSetting] = collector,
            [TelemetryHealthSettings.ElasticsearchHealthUrlSetting] = elasticsearch,
        })).ShouldBeNull();

    [Fact]
    public void A_url_that_is_not_absolute_http_stops_the_host_naming_the_setting_only()
    {
        const string bad = "ftp://monitor:secret@localhost/";
        var exception = Should.Throw<InvalidOperationException>(() => TelemetryHealthSettings.FromConfiguration(Configuration(new()
        {
            [TelemetryHealthSettings.CollectorHealthUrlSetting] = Collector,
            [TelemetryHealthSettings.ElasticsearchHealthUrlSetting] = bad,
        })));

        exception.Message.ShouldContain(TelemetryHealthSettings.ElasticsearchHealthUrlSetting);
        exception.Message.ShouldNotContain("secret");
        exception.Message.ShouldNotContain(bad);
    }

    [Fact]
    public void The_settings_never_print_the_password()
    {
        var settings = TelemetryHealthSettings.FromConfiguration(Configuration(new()
        {
            [TelemetryHealthSettings.CollectorHealthUrlSetting] = Collector,
            [TelemetryHealthSettings.ElasticsearchHealthUrlSetting] = Elasticsearch,
            [TelemetryHealthSettings.ElasticsearchUserSetting] = "waslabid_monitor",
            [TelemetryHealthSettings.ElasticsearchPasswordSetting] = "not-a-real-password",
        }));

        settings!.ToString()!.ShouldNotContain("not-a-real-password");
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
