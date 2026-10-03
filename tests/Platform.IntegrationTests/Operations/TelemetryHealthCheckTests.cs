using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations;
using Platform.Modules.Operations.Alerts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Operations.Health;
using Platform.Shared;
using Platform.Shared.Telemetry;
using CheckStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;
using ResultStatus = Platform.Modules.Operations.Contracts.HealthStatus;

namespace Platform.IntegrationTests.Operations;

/// <summary>
/// W-10, plan task 4 (O-14): the Telemetry check of the worker's health job. The collector's <c>health_check</c> extension
/// and Elasticsearch's <c>/_cluster/health</c> are <see cref="FakeHttpServer"/> doubles (the Compose services come with plan
/// task 8). A dead pipeline opens an incident and sends emails through F-60 like Disk does; the check names which part
/// failed and never reports the monitoring credentials, a URL or a response body (N-10). Own Mailpit, emptied before each
/// test; the component name "Telemetry" is recorded by the outage test alone, every other test records under a name of its own.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TelemetryHealthCheckTests(DatabaseFixture db, MailpitFixture mailpit) : IClassFixture<MailpitFixture>, IAsyncLifetime
{
    private const string MonitorUser = "monitor-user-4be1c9";
    private const string MonitorPassword = "monitor-password-should-never-appear-7d20";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        using var httpClient = new HttpClient { BaseAddress = mailpit.ApiBaseAddress };
        using var response = await httpClient.DeleteAsync("/api/v1/messages", Ct);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData("green")]
    [InlineData("yellow")]
    public async Task Telemetry_reports_healthy_when_the_collector_answers_and_the_cluster_is_green_or_yellow(string clusterStatus)
    {
        await using var collector = await FakeHttpServer.StartAsync(context => AnswerAsync(context, 200, """{"status":"Server available"}"""), Ct);
        await using var elasticsearch = await FakeHttpServer.StartAsync(Elasticsearch(Cluster(clusterStatus)), Ct);

        var result = await CheckAsync(collector.BaseAddress, elasticsearch.BaseAddress + "_cluster/health");

        result.Status.ShouldBe(CheckStatus.Healthy);
    }

    [Theory]
    [InlineData("collector answering 503")]
    [InlineData("elasticsearch unreachable")]
    [InlineData("elasticsearch red")]
    [InlineData("both down")]
    public async Task Telemetry_reports_unhealthy_naming_the_collector_or_elasticsearch_when_either_is_down(string outage)
    {
        var collectorDown = outage is "collector answering 503" or "both down";
        var elasticsearchUnreachable = outage is "elasticsearch unreachable" or "both down";
        await using var collector = await FakeHttpServer.StartAsync(
            context => AnswerAsync(context, collectorDown ? 503 : 200, """{"status":"Server not available"}"""), Ct);
        await using var elasticsearch = await FakeHttpServer.StartAsync(
            Elasticsearch(Cluster(outage == "elasticsearch red" ? "red" : "green")), Ct);
        var elasticsearchUrl = elasticsearchUnreachable
            ? $"http://127.0.0.1:{PlatformWebFactory.UnusedLoopbackPort()}/_cluster/health"
            : elasticsearch.BaseAddress + "_cluster/health";

        var result = await CheckAsync(collector.BaseAddress, elasticsearchUrl);

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        var message = result.Description.ShouldNotBeNull();
        message.ShouldStartWith("Telemetry pipeline: ");
        if (collectorDown)
        {
            message.ShouldContain("collector answered HTTP 503");
        }
        else
        {
            message.ShouldNotContain("collector");
        }

        switch (outage)
        {
            case "elasticsearch unreachable" or "both down":
                message.ShouldContain("elasticsearch could not be reached (HttpRequestException)");
                break;
            case "elasticsearch red":
                message.ShouldContain("elasticsearch cluster status is red");
                break;
            default:
                message.ShouldNotContain("elasticsearch");
                break;
        }

        // Never a URL (N-10): a configured URL could carry credentials.
        message.ShouldNotContain("://");
        message.ShouldNotContain("127.0.0.1");
    }

    /// <summary>
    /// W-10 follow-up (2026-10-02; O-14): at the flood-stage disk watermark Elasticsearch makes every index read-only while the
    /// cluster stays green, so the pipeline drops every document with a healthy cluster. The check reads each node's file
    /// system totals (<c>_nodes/stats/fs</c>, cluster privilege <c>monitor</c>; fix round 1) and reports the fullest node, in
    /// use = total minus available, as Elasticsearch computes its watermarks: Degraded from the high watermark (90%, new
    /// shards no longer allocated there), Unhealthy from the flood stage (95%, writes blocked). A node without totals is
    /// skipped.
    /// </summary>
    [Theory]
    [InlineData("11", CheckStatus.Healthy, null)]
    [InlineData("89", CheckStatus.Healthy, null)]
    [InlineData("90", CheckStatus.Degraded, "elasticsearch disk 90% used, at or above the high watermark (90%)")]
    [InlineData("94", CheckStatus.Degraded, "elasticsearch disk 94% used, at or above the high watermark (90%)")]
    [InlineData("95", CheckStatus.Unhealthy, "elasticsearch disk 95% used, at or above the flood stage (95%): indices are read-only")]
    [InlineData("99", CheckStatus.Unhealthy, "elasticsearch disk 99% used, at or above the flood stage (95%): indices are read-only")]
    public async Task Telemetry_reports_the_fullest_elasticsearch_disk_against_the_watermarks(string percent, CheckStatus expected, string? message)
    {
        await using var collector = await FakeHttpServer.StartAsync(context => AnswerAsync(context, 200, """{"status":"Server available"}"""), Ct);
        var nodes = """{"nodes":{"a":""" + FileSystem("12") + ""","b":""" + FileSystem(percent) + ""","c":{"fs":{}}}}""";
        await using var elasticsearch = await FakeHttpServer.StartAsync(Elasticsearch(Cluster("green"), nodes), Ct);

        var result = await CheckAsync(collector.BaseAddress, elasticsearch.BaseAddress + "_cluster/health");

        result.Status.ShouldBe(expected);
        if (message is null)
        {
            result.Description.ShouldBeNull();
        }
        else
        {
            result.Description.ShouldBe($"Telemetry pipeline: {message}.");
        }
    }

    [Fact]
    public async Task A_full_disk_is_reported_beside_a_collector_outage()
    {
        await using var collector = await FakeHttpServer.StartAsync(context => AnswerAsync(context, 503, "{}"), Ct);
        await using var elasticsearch = await FakeHttpServer.StartAsync(Elasticsearch(Cluster("green"), Nodes("91")), Ct);

        var result = await CheckAsync(collector.BaseAddress, elasticsearch.BaseAddress + "_cluster/health");

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        result.Description.ShouldBe(
            "Telemetry pipeline: collector answered HTTP 503; elasticsearch disk 91% used, at or above the high watermark (90%).");
    }

    /// <summary>
    /// An answer the check cannot read is Unhealthy, as an unreadable cluster health is: whether writes are blocked is
    /// unknown. Only the HTTP status or the exception type is named, never the body (it may echo anything, N-10).
    /// </summary>
    [Theory]
    [InlineData(200, "not json at all", "elasticsearch disk usage is unreadable (JsonReaderException)")]
    [InlineData(200, """[{"disk.percent":"97"}]""", "elasticsearch disk usage is unreadable")]
    [InlineData(200, "{}", "elasticsearch disk usage is unreadable")]
    [InlineData(200, """{"nodes":{}}""", "elasticsearch disk usage is unreadable")]
    [InlineData(200, """{"nodes":{"a":{"fs":{"total":{"total_in_bytes":0,"available_in_bytes":0}}}}}""", "elasticsearch disk usage is unreadable")]
    [InlineData(200, """{"nodes":{"a":{"fs":{"total":{"total_in_bytes":"monitor-password-should-never-appear-7d20","available_in_bytes":1}}}}}""", "elasticsearch disk usage is unreadable")]
    [InlineData(403, """{"error":"monitor-user-4be1c9 lacks a privilege"}""", "elasticsearch disk usage answered HTTP 403")]
    public async Task An_unreadable_disk_answer_is_unhealthy_and_names_no_body(int status, string body, string message)
    {
        await using var collector = await FakeHttpServer.StartAsync(context => AnswerAsync(context, 200, """{"status":"Server available"}"""), Ct);
        await using var elasticsearch = await FakeHttpServer.StartAsync(Elasticsearch(Cluster("green"), body, status), Ct);

        var result = await CheckAsync(collector.BaseAddress, elasticsearch.BaseAddress + "_cluster/health");

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        result.Description.ShouldBe($"Telemetry pipeline: {message}.");
    }

    /// <summary>
    /// The disk is read beside the configured cluster health URL, under any path prefix (a proxy), with the monitoring
    /// credentials, filtered to the file system totals the check needs.
    /// </summary>
    [Fact]
    public async Task The_disk_is_read_beside_the_cluster_health_url_with_the_monitoring_credentials()
    {
        await using var collector = await FakeHttpServer.StartAsync(context => AnswerAsync(context, 200, """{"status":"Server available"}"""), Ct);
        var requests = new List<(string PathAndQuery, string Authorization)>();
        var answer = Elasticsearch(Cluster("green"));
        await using var elasticsearch = await FakeHttpServer.StartAsync(
            context =>
            {
                lock (requests)
                {
                    requests.Add((context.Request.Path + context.Request.QueryString, context.Request.Headers.Authorization.ToString()));
                }

                return answer(context);
            },
            Ct);

        var result = await CheckAsync(collector.BaseAddress, elasticsearch.BaseAddress + "es/_cluster/health/");

        result.Status.ShouldBe(CheckStatus.Healthy);
        var expected = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{MonitorUser}:{MonitorPassword}"))).ToString();
        requests.Select(r => r.PathAndQuery).Order().ShouldBe(["/es/_cluster/health/", "/es/_nodes/stats/fs?filter_path=nodes.*.fs.total"]);
        requests.ShouldAllBe(r => r.Authorization == expected);
    }

    /// <summary>
    /// Fix round 1: the disk is read at the same time as the cluster health (one budget), and when the cluster is red or
    /// cannot be read the result names only the cluster, whatever the disk says.
    /// </summary>
    [Theory]
    [InlineData("red", "elasticsearch cluster status is red")]
    [InlineData("purple", "elasticsearch cluster status is unknown")]
    public async Task A_red_or_unknown_cluster_is_reported_alone_beside_a_full_disk(string clusterStatus, string message)
    {
        await using var collector = await FakeHttpServer.StartAsync(context => AnswerAsync(context, 200, """{"status":"Server available"}"""), Ct);
        await using var elasticsearch = await FakeHttpServer.StartAsync(Elasticsearch(Cluster(clusterStatus), Nodes("99")), Ct);

        var result = await CheckAsync(collector.BaseAddress, elasticsearch.BaseAddress + "_cluster/health");

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        result.Description.ShouldBe($"Telemetry pipeline: {message}.");
    }

    /// <summary>Both requests start before either answers: a slow cluster health does not delay the disk read.</summary>
    [Fact]
    public async Task The_cluster_health_and_the_disk_are_read_at_the_same_time()
    {
        await using var collector = await FakeHttpServer.StartAsync(context => AnswerAsync(context, 200, """{"status":"Server available"}"""), Ct);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var answer = Elasticsearch(Cluster("green"));
        await using var elasticsearch = await FakeHttpServer.StartAsync(
            async context =>
            {
                if (Interlocked.Increment(ref arrived) == 2)
                {
                    both.TrySetResult();
                }

                // Each answer waits for the other request: a sequential check would wait 2 s and then fail on this timeout.
                await both.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
                await answer(context);
            },
            Ct);

        var result = await CheckAsync(collector.BaseAddress, elasticsearch.BaseAddress + "_cluster/health");

        result.Status.ShouldBe(CheckStatus.Healthy);
    }

    [Fact]
    public async Task The_telemetry_check_sends_the_monitoring_credentials_and_never_reports_them()
    {
        string? collectorAuthorization = "(not called)";
        string? elasticsearchAuthorization = null;
        await using var collector = await FakeHttpServer.StartAsync(
            context =>
            {
                collectorAuthorization = context.Request.Headers.Authorization.ToString();
                return AnswerAsync(context, 200, """{"status":"Server available"}""");
            },
            Ct);
        // A red cluster, so there is a message to inspect; the body echoes the credentials to prove it is never reported.
        await using var elasticsearch = await FakeHttpServer.StartAsync(
            context =>
            {
                elasticsearchAuthorization = context.Request.Headers.Authorization.ToString();
                return AnswerAsync(context, 200, $$"""{"status":"red","cluster_name":"{{MonitorUser}}","note":"{{MonitorPassword}}"}""");
            },
            Ct);

        var telemetry = new CapturedTelemetry();
        using var host = await StartHostAsync(telemetry, collector.BaseAddress, elasticsearch.BaseAddress + "_cluster/health");
        var component = $"telemetry-credentials-{Guid.NewGuid():N}";
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var registered = RegisteredTelemetryCheck(scope.ServiceProvider);
            await NewJob(scope.ServiceProvider, new NamedHealthCheck(component, registered)).RunAsync(Ct);
        }

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{MonitorUser}:{MonitorPassword}"));
        elasticsearchAuthorization.ShouldBe(new AuthenticationHeaderValue("Basic", expected).ToString());
        collectorAuthorization.ShouldBe(string.Empty, "the collector gets no credentials");

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var healthLog = scope.ServiceProvider.GetRequiredService<IHealthLog>();
            var result = (await healthLog.LatestAsync(Ct)).Single(r => r.Component == component);
            result.Status.ShouldBe(ResultStatus.Unhealthy);
            result.Message.ShouldNotBeNull().ShouldContain("elasticsearch cluster status is red");
            ShouldNotLeak(result.Message);

            var incident = (await healthLog.IncidentsAsync(DateTimeOffset.UtcNow.AddHours(-1), Ct)).Single(i => i.Component == component);
            ShouldNotLeak(incident.LastMessage);
        }

        var email = await WaitForSubjectAsync($"[WaslaBid] {component} is down", 1);
        email.Count.ShouldBe(1);
        ShouldNotLeak(await TextOfAsync(email[0]));

        telemetry.Logs.ShouldNotBeEmpty();
        foreach (var log in telemetry.Logs)
        {
            var where = $"a {log.Level} record of {log.Category} ({log.Template})";
            ShouldNotLeak(log.Message, where);
            foreach (var (key, value) in log.Properties)
            {
                ShouldNotLeak(value, $"{where}, property {key}");
            }
        }

        foreach (var span in telemetry.AllSpans)
        {
            foreach (var tag in span.Tags)
            {
                ShouldNotLeak(tag.Value);
            }

            ShouldNotLeak(span.StatusDescription);
        }
    }

    [Fact]
    public async Task A_telemetry_outage_opens_one_incident_and_sends_one_alert_and_one_recovery()
    {
        var collectorStatus = 503;
        await using var collector = await FakeHttpServer.StartAsync(
            context => AnswerAsync(context, Volatile.Read(ref collectorStatus), """{"status":"Server not available"}"""), Ct);
        await using var elasticsearch = await FakeHttpServer.StartAsync(Elasticsearch(Cluster("green")), Ct);

        using var host = await StartHostAsync(new CapturedTelemetry(), collector.BaseAddress, elasticsearch.BaseAddress + "_cluster/health");
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        // Down on two runs (one incident, one email), then back (the incident closes, one recovery email).
        await RunTelemetryAsync(host);
        await RunTelemetryAsync(host);
        Volatile.Write(ref collectorStatus, 200);
        await RunTelemetryAsync(host);

        (await WaitForSubjectAsync("[WaslaBid] Telemetry has recovered", 1)).Count.ShouldBe(1);
        var down = await MessagesWithSubjectAsync("[WaslaBid] Telemetry is down");
        down.Count.ShouldBe(1);
        var text = await TextOfAsync(down[0]);
        text.ShouldContain("Component: Telemetry");
        text.ShouldContain("collector answered HTTP 503");

        await using var scope = host.Services.CreateAsyncScope();
        var incidents = (await scope.ServiceProvider.GetRequiredService<IHealthLog>().IncidentsAsync(startedAt, Ct))
            .Where(i => i.Component == HealthComponents.Telemetry)
            .ToList();
        incidents.Count.ShouldBe(1);
        incidents[0].ClosedAt.ShouldNotBeNull();
    }

    private static async Task RunTelemetryAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var registered = scope.ServiceProvider.GetServices<NamedHealthCheck>().Single(c => c.Component == HealthComponents.Telemetry);
        await NewJob(scope.ServiceProvider, registered).RunAsync(Ct);
    }

    private static async Task<HealthCheckResult> CheckAsync(string collectorUrl, string elasticsearchUrl)
    {
        using var httpClient = new HttpClient();
        var check = new TelemetryHealthCheck(
            httpClient, new TelemetryHealthSettings(new Uri(collectorUrl), new Uri(elasticsearchUrl), MonitorUser, MonitorPassword));
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        ShouldNotLeak(result.Description);
        return result;
    }

    /// <summary>
    /// A worker-like host: the telemetry pipeline (Serilog with the captured sink, the in-memory span exporter), the
    /// Operations module, alerts to this class's Mailpit, and the Telemetry check registered from configuration only.
    /// </summary>
    private async Task<IHost> StartHostAsync(CapturedTelemetry telemetry, string collectorUrl, string elasticsearchUrl)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryModule.OtlpEndpointSetting] = string.Empty,
            ["Smtp:Host"] = mailpit.SmtpHost,
            ["Smtp:Port"] = mailpit.SmtpPort.ToString(CultureInfo.InvariantCulture),
            ["Smtp:From"] = "alerts@waslabid.test",
            ["Platform:AlertRecipients:0"] = "platform-admin@waslabid.test",
            [TelemetryHealthSettings.CollectorHealthUrlSetting] = collectorUrl,
            [TelemetryHealthSettings.ElasticsearchHealthUrlSetting] = elasticsearchUrl,
            [TelemetryHealthSettings.ElasticsearchUserSetting] = MonitorUser,
            [TelemetryHealthSettings.ElasticsearchPasswordSetting] = MonitorPassword,
        });
        builder.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        builder.AddPlatformTelemetry(TelemetryNames.Services.Worker);
        telemetry.AddTo(builder.Services);
        builder.Services.AddPlatformShared();
        builder.Services.AddOperationsModule(db.WorkerConnectionString);
        builder.Services.AddOperationsAlerts(builder.Configuration);
        builder.Services.AddTelemetryHealthCheck(builder.Configuration);
        builder.Services.AddHealthCheckJob();

        var host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private static IHealthCheck RegisteredTelemetryCheck(IServiceProvider services) =>
        services.GetServices<NamedHealthCheck>().Single(c => c.Component == HealthComponents.Telemetry).Check;

    /// <summary>The job with only the given check, so no other component is recorded in the shared database.</summary>
    private static HealthCheckJob NewJob(IServiceProvider services, NamedHealthCheck check) =>
        new(
            [check],
            services.GetRequiredService<IHealthLog>(),
            services.GetRequiredService<IncidentNotifier>(),
            new FallbackAlertState(),
            services.GetRequiredService<IAlertSender>(),
            services.GetRequiredService<AlertSettings>(),
            services.GetRequiredService<HealthTelemetry>(),
            TimeProvider.System,
            services.GetRequiredService<ILogger<HealthCheckJob>>());

    private static void ShouldNotLeak(object? value, string? where = null)
    {
        var text = value switch
        {
            null => string.Empty,
            string s => s,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        text.ShouldNotContain(MonitorPassword, customMessage: where);
        text.ShouldNotContain(MonitorUser, customMessage: where);
        text.ShouldNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{MonitorUser}:{MonitorPassword}")), customMessage: where);
    }

    private static string Cluster(string status) => $$"""{"cluster_name":"waslabid","status":"{{status}}"}""";

    /// <summary>One node's file system totals with <paramref name="percent"/> of 1,000,000 bytes in use.</summary>
    private static string FileSystem(string percent) =>
        """{"fs":{"total":{"total_in_bytes":1000000,"free_in_bytes":1,"available_in_bytes":"""
        + (1000000 - (int.Parse(percent, CultureInfo.InvariantCulture) * 10000)).ToString(CultureInfo.InvariantCulture) + "}}}";

    private static string Nodes(string percent) => """{"nodes":{"wpZ4fewcSD6fcYOt0Y-dng":""" + FileSystem(percent) + "}}";

    /// <summary>
    /// Elasticsearch: <c>_nodes/stats/fs</c> answers <paramref name="nodes"/> with <paramref name="nodesStatus"/> (11% used by
    /// default), any other path the cluster health <paramref name="cluster"/>.
    /// </summary>
    private static Func<HttpContext, Task> Elasticsearch(string cluster, string? nodes = null, int nodesStatus = 200) =>
        context => context.Request.Path.Value!.EndsWith("/_nodes/stats/fs", StringComparison.Ordinal)
            ? AnswerAsync(context, nodesStatus, nodes ?? Nodes("11"))
            : AnswerAsync(context, 200, cluster);

    private static async Task AnswerAsync(HttpContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body);
    }

    private async Task<List<JsonElement>> WaitForSubjectAsync(string subject, int expectedCount, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var matches = await MessagesWithSubjectAsync(subject);
            if (matches.Count >= expectedCount || DateTime.UtcNow > deadline)
            {
                return matches;
            }

            await Task.Delay(200, Ct);
        }
    }

    private async Task<List<JsonElement>> MessagesWithSubjectAsync(string subject)
    {
        using var httpClient = new HttpClient { BaseAddress = mailpit.ApiBaseAddress };
        using var response = await httpClient.GetAsync("/api/v1/messages?limit=500", Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. doc.RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("Subject").GetString() == subject)
            .Select(m => m.Clone())];
    }

    private async Task<string> TextOfAsync(JsonElement message)
    {
        using var httpClient = new HttpClient { BaseAddress = mailpit.ApiBaseAddress };
        using var response = await httpClient.GetAsync($"/api/v1/message/{message.GetProperty("ID").GetString()}", Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("Text").GetString() ?? string.Empty;
    }
}
