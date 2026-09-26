using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Platform.Modules.Operations.Health;

namespace Platform.UnitTests.Operations;

/// <summary>
/// F-60 disk alert: the check measures <c>Platform:DiskPath</c> (the volume that holds PostgreSQL or object storage
/// data in the deployment), falling back to the worker's content root only when the setting is absent.
/// </summary>
public sealed class DiskSpaceHealthCheckTests
{
    private const string ConnectionString = "Host=localhost;Database=platform";

    [Fact]
    public void A_configured_disk_path_is_the_one_measured()
    {
        var configured = Path.GetTempPath();
        var settings = HealthCheckSettings.FromConfiguration(
            Configuration(new() { ["Platform:DiskPath"] = configured }), ConnectionString);

        settings.DiskPathOr(contentRootPath: "/somewhere/else").ShouldBe(configured);
    }

    [Fact]
    public void Without_a_configured_path_the_content_root_is_measured()
    {
        var settings = HealthCheckSettings.FromConfiguration(Configuration([]), ConnectionString);

        settings.DiskPathOr(contentRootPath: "/app").ShouldBe("/app");
    }

    [Fact]
    public async Task The_check_reports_on_the_configured_path()
    {
        var configured = Path.GetTempPath();
        var settings = HealthCheckSettings.FromConfiguration(
            Configuration(new() { ["Platform:DiskPath"] = configured }), ConnectionString);

        // A zero percent threshold makes any real drive "at or above", so the message shows what was measured.
        var check = new DiskSpaceHealthCheck(settings.DiskPathOr(contentRootPath: "/somewhere/else"), thresholdPercent: 0);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        var expectedVolume = OperatingSystem.IsWindows() ? Path.GetPathRoot(configured)! : Path.GetFullPath(configured);
        result.Description.ShouldContain($"on {expectedVolume} is");
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
