using System.Globalization;
using System.Text.Json;
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
using Platform.Shared.Caching;
using StackExchange.Redis;
using CheckStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Platform.IntegrationTests.Operations;

/// <summary>
/// W-34: Redis is checked by the worker's health job and alerted through F-60 like Disk and Telemetry, not shown as a
/// board tile. The check is registered only where <c>ConnectionStrings:Redis</c> is set (CI without Redis neither runs
/// nor alerts on it), PINGs through the host's one multiplexer, and names only the exception type when Redis does not
/// answer: never the connection string, its password or the endpoint (N-10). Own Redis and Mailpit; the component name
/// "Redis" is recorded by the outage test alone, every other test records under a name of its own.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RedisHealthCheckTests(DatabaseFixture db, RedisFixture redis, MailpitFixture mailpit)
    : IClassFixture<RedisFixture>, IClassFixture<MailpitFixture>, IAsyncLifetime
{
    private const string Password = "redis-password-should-never-appear-91c3";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        using var httpClient = new HttpClient { BaseAddress = mailpit.ApiBaseAddress };
        using var response = await httpClient.DeleteAsync("/api/v1/messages", Ct);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void Redis_is_alerted_but_is_not_a_board_tile()
    {
        HealthComponents.Redis.ShouldBe("Redis");
        HealthComponents.Board.ShouldNotContain(HealthComponents.Redis);
    }

    [Fact]
    public async Task Redis_reports_healthy_when_it_answers_ping()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(RedisConnection.Options(redis.ConnectionString));

        var result = await new RedisHealthCheck(multiplexer).CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(CheckStatus.Healthy);
    }

    [Fact]
    public async Task Redis_reports_unhealthy_naming_only_the_exception_type_when_it_does_not_answer()
    {
        var port = PlatformWebFactory.UnusedLoopbackPort();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(RedisConnection.Options($"127.0.0.1:{port},password={Password}"));

        var result = await new RedisHealthCheck(multiplexer).CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        var message = result.Description.ShouldNotBeNull();
        message.ShouldContain("Could not reach Redis.");
        message.ShouldNotContain(Password);
        message.ShouldNotContain("127.0.0.1");
        message.ShouldNotContain(port.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_check_is_registered_only_when_the_connection_string_is_set()
    {
        using (var without = await StartHostAsync(connectionString: null))
        {
            without.Services.GetServices<NamedHealthCheck>().ShouldNotContain(c => c.Component == HealthComponents.Redis);
            without.Services.GetService<IConnectionMultiplexer>().ShouldBeNull();
        }

        using var with = await StartHostAsync(redis.ConnectionString);
        var check = with.Services.GetServices<NamedHealthCheck>().Where(c => c.Component == HealthComponents.Redis).ShouldHaveSingleItem();
        (await check.Check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(CheckStatus.Healthy);
    }

    [Fact]
    public async Task A_redis_outage_opens_one_incident_and_sends_one_alert_without_the_connection_string()
    {
        var port = PlatformWebFactory.UnusedLoopbackPort();
        using var host = await StartHostAsync($"127.0.0.1:{port},password={Password}");
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        await RunRedisAsync(host);
        await RunRedisAsync(host);

        var down = await WaitForSubjectAsync("[WaslaBid] Redis is down", 1);
        down.Count.ShouldBe(1);
        var text = await TextOfAsync(down[0]);
        text.ShouldContain("Component: Redis");
        text.ShouldContain("Could not reach Redis.");
        text.ShouldNotContain(Password);
        text.ShouldNotContain(port.ToString(CultureInfo.InvariantCulture));

        await using var scope = host.Services.CreateAsyncScope();
        var incidents = (await scope.ServiceProvider.GetRequiredService<IHealthLog>().IncidentsAsync(startedAt, Ct))
            .Where(i => i.Component == HealthComponents.Redis)
            .ToList();
        var incident = incidents.ShouldHaveSingleItem();
        incident.LastMessage.ShouldNotBeNull().ShouldNotContain(Password);

        // Close it again so the shared database holds no open Redis incident for other tests.
        await RunWithAsync(host, new NamedHealthCheck(HealthComponents.Redis, new FixedHealthCheck(HealthCheckResult.Healthy())));
    }

    private async Task<IHost> StartHostAsync(string? connectionString)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Testing" });
        var settings = new Dictionary<string, string?>
        {
            ["Smtp:Host"] = mailpit.SmtpHost,
            ["Smtp:Port"] = mailpit.SmtpPort.ToString(CultureInfo.InvariantCulture),
            ["Smtp:From"] = "alerts@waslabid.test",
            ["Platform:AlertRecipients:0"] = "platform-admin@waslabid.test",
        };
        if (connectionString is not null)
        {
            settings[$"ConnectionStrings:{RedisConnection.ConnectionStringName}"] = connectionString;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.Services.AddPlatformShared();
        builder.Services.AddOperationsModule(db.AppConnectionString);
        builder.Services.AddOperationsAlerts(builder.Configuration);
        builder.Services.AddRedisHealthCheck(builder.Configuration);
        builder.Services.AddHealthCheckJob();
        var host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private static async Task RunRedisAsync(IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await RunWithAsync(host, scope.ServiceProvider.GetServices<NamedHealthCheck>().Single(c => c.Component == HealthComponents.Redis));
    }

    /// <summary>The job with only the given check, so no other component is recorded in the shared database.</summary>
    private static async Task RunWithAsync(IHost host, NamedHealthCheck check)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await new HealthCheckJob(
            [check],
            services.GetRequiredService<IHealthLog>(),
            services.GetRequiredService<IncidentNotifier>(),
            new FallbackAlertState(),
            services.GetRequiredService<IAlertSender>(),
            services.GetRequiredService<AlertSettings>(),
            services.GetRequiredService<HealthTelemetry>(),
            TimeProvider.System,
            services.GetRequiredService<ILogger<HealthCheckJob>>()).RunAsync(Ct);
    }

    private async Task<List<JsonElement>> WaitForSubjectAsync(string subject, int expectedCount, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            using var httpClient = new HttpClient { BaseAddress = mailpit.ApiBaseAddress };
            using var response = await httpClient.GetAsync("/api/v1/messages?limit=500", Ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            List<JsonElement> matches = [.. doc.RootElement.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("Subject").GetString() == subject)
                .Select(m => m.Clone())];
            if (matches.Count >= expectedCount || DateTime.UtcNow > deadline)
            {
                return matches;
            }

            await Task.Delay(200, Ct);
        }
    }

    private async Task<string> TextOfAsync(JsonElement message)
    {
        using var httpClient = new HttpClient { BaseAddress = mailpit.ApiBaseAddress };
        using var response = await httpClient.GetAsync($"/api/v1/message/{message.GetProperty("ID").GetString()}", Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("Text").GetString() ?? string.Empty;
    }

    private sealed class FixedHealthCheck(HealthCheckResult result) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
}
