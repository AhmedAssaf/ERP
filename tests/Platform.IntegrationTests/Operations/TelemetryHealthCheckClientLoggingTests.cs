using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Operations.Health;
using CheckStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Platform.IntegrationTests.Operations;

/// <summary>
/// W-10 follow-up (final review, task 4; closed 2026-10-03), N-10: the Telemetry check's HTTP client has the factory's
/// loggers removed, so the check writes no request records at all. Since .NET 9 the factory's loggers redact header values
/// by default, so the credential assertion alone would pass without <c>RemoveAllLoggers()</c>; what catches its removal is
/// the category assertion: with every category at Trace, a check that sends the credentials leaves no record from the
/// client's <c>System.Net.Http.HttpClient.*</c> categories (and none holding the user, the password or their base64 form).
/// No database or host is started.
/// </summary>
public sealed class TelemetryHealthCheckClientLoggingTests
{
    private const string MonitorUser = "monitor-user-client-logs-61c2";
    private const string MonitorPassword = "monitor-password-client-logs-never-logged-0a9e";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_check_client_writes_no_request_record_even_at_trace()
    {
        await using var collector = await FakeHttpServer.StartAsync(context => AnswerAsync(context, """{"status":"Server available"}"""), Ct);
        await using var elasticsearch = await FakeHttpServer.StartAsync(
            context => context.Request.Path.Value!.EndsWith("/_nodes/stats/fs", StringComparison.Ordinal)
                ? AnswerAsync(context, """{"nodes":{"n1":{"fs":{"total":{"total_in_bytes":100,"free_in_bytes":90,"available_in_bytes":90}}}}}""")
                : AnswerAsync(context, """{"cluster_name":"waslabid","status":"green"}"""),
            Ct);
        var logs = new CapturedLogs();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryHealthSettings.CollectorHealthUrlSetting] = collector.BaseAddress,
            [TelemetryHealthSettings.ElasticsearchHealthUrlSetting] = elasticsearch.BaseAddress + "_cluster/health",
            [TelemetryHealthSettings.ElasticsearchUserSetting] = MonitorUser,
            [TelemetryHealthSettings.ElasticsearchPasswordSetting] = MonitorPassword,
        }).Build();
        var services = new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs))
            .AddTelemetryHealthCheck(configuration);
        await using var provider = services.BuildServiceProvider();

        var check = provider.GetServices<NamedHealthCheck>().Single(c => c.Component == HealthComponents.Telemetry).Check;
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(CheckStatus.Healthy, "the check reached both doubles, so the client sent its requests");
        logs.Entries.ShouldNotContain(e => e.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{MonitorUser}:{MonitorPassword}"));
        logs.Entries.ShouldAllBe(e => !e.Text.Contains(MonitorPassword) && !e.Text.Contains(MonitorUser) && !e.Text.Contains(basic));
    }

    private static async Task AnswerAsync(HttpContext context, string body)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body);
    }
}
