using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations;
using Platform.Modules.Operations.Alerts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Operations.Health;
using Platform.Shared;
using Testcontainers.PostgreSql;
using CheckStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;
using ResultStatus = Platform.Modules.Operations.Contracts.HealthStatus;

namespace Platform.IntegrationTests.Operations;

/// <summary>
/// F-60 acceptance (docs/09): "Given PostgreSQL stopped, when the next health check fails, then the platform admin
/// receives one email naming the component within two minutes", plus the no-lost-alert rule: a failed send is retried
/// on the next run and nothing is ever sent twice. Own Mailpit per class, emptied before each test, so counts are exact.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AlertFallbackTests(DatabaseFixture db, MailpitFixture mailpit) : IClassFixture<MailpitFixture>, IAsyncLifetime
{
    private const string StoreSubject = "[WaslaBid] WaslaBid cannot record health results";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        using var httpClient = new HttpClient { BaseAddress = mailpit.ApiBaseAddress };
        using var response = await httpClient.DeleteAsync("/api/v1/messages", Ct);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task With_the_store_down_each_unhealthy_component_is_alerted_once_and_recovers_once()
    {
        using var host = await StartHostAsync(db.AppConnectionString);
        var postgres = new SwitchableCheck(CheckStatus.Unhealthy, "NpgsqlException: Could not reach PostgreSQL.");
        var healthLog = new SwitchableHealthLog { Down = true };
        var fallback = new FallbackAlertState();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var job = NewJob(scope.ServiceProvider, [new NamedHealthCheck("PostgreSQL", postgres)], healthLog, fallback);
            await job.RunAsync(Ct);
            await job.RunAsync(Ct);
        }

        (await MessagesWithSubjectAsync("[WaslaBid] PostgreSQL is down")).Count.ShouldBe(1);
        var store = await MessagesWithSubjectAsync(StoreSubject);
        store.Count.ShouldBe(1);
        var storeText = await TextOfAsync(store[0]);
        storeText.ShouldContain(nameof(InvalidOperationException));
        storeText.ShouldNotContain(SwitchableHealthLog.SecretInMessage);

        healthLog.Down = false;
        postgres.Status = CheckStatus.Healthy;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var job = NewJob(scope.ServiceProvider, [new NamedHealthCheck("PostgreSQL", postgres)], healthLog, fallback);
            await job.RunAsync(Ct);
            await job.RunAsync(Ct);
        }

        (await MessagesWithSubjectAsync("[WaslaBid] PostgreSQL has recovered")).Count.ShouldBe(1);
        (await MessagesWithSubjectAsync("[WaslaBid] PostgreSQL is down")).Count.ShouldBe(1);
        (await MessagesWithSubjectAsync(StoreSubject)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task When_the_store_returns_an_incident_already_announced_by_the_fallback_is_not_announced_again()
    {
        var component = $"fallback-component-{Guid.NewGuid():N}";
        using var host = await StartHostAsync(db.AppConnectionString);
        var check = new SwitchableCheck(CheckStatus.Unhealthy, "Could not reach it.");
        var healthLog = new SwitchableHealthLog { Down = true };
        var fallback = new FallbackAlertState();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await NewJob(scope.ServiceProvider, [new NamedHealthCheck(component, check)], healthLog, fallback).RunAsync(Ct);
        }

        // The store is back but the component is still down: the incident opens in the database, already announced.
        healthLog.Down = false;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            healthLog.Inner = scope.ServiceProvider.GetRequiredService<IHealthLog>();
            await NewJob(scope.ServiceProvider, [new NamedHealthCheck(component, check)], healthLog, fallback).RunAsync(Ct);
        }

        (await MessagesWithSubjectAsync($"[WaslaBid] {component} is down")).Count.ShouldBe(1);

        // Its later recovery goes through the normal incident pipeline: exactly one recovery notice.
        check.Status = CheckStatus.Healthy;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            healthLog.Inner = scope.ServiceProvider.GetRequiredService<IHealthLog>();
            await NewJob(scope.ServiceProvider, [new NamedHealthCheck(component, check)], healthLog, fallback).RunAsync(Ct);
        }

        (await WaitForSubjectAsync($"[WaslaBid] {component} has recovered", 1)).Count.ShouldBe(1);
        (await MessagesWithSubjectAsync($"[WaslaBid] {component} is down")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_stopped_database_still_sends_one_PostgreSQL_down_email()
    {
        await using var container = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();
        await container.StartAsync(Ct);
        var stoppedConnectionString = container.GetConnectionString();
        await container.StopAsync(Ct);

        // Everything that touches the database (the health log and the notifier) points at the stopped server.
        using var host = await StartHostAsync(stoppedConnectionString);
        await using var scope = host.Services.CreateAsyncScope();
        var job = NewJob(
            scope.ServiceProvider,
            [new NamedHealthCheck("PostgreSQL", new PostgreSqlHealthCheck(stoppedConnectionString))],
            scope.ServiceProvider.GetRequiredService<IHealthLog>(),
            new FallbackAlertState());

        await job.RunAsync(Ct);

        var messages = await WaitForSubjectAsync("[WaslaBid] PostgreSQL is down", 1, timeoutSeconds: 120);
        messages.Count.ShouldBe(1);
        (await TextOfAsync(messages[0])).ShouldContain("Component: PostgreSQL");
    }

    [Fact]
    public async Task A_failed_send_is_retried_on_the_next_run_and_nothing_is_sent_twice()
    {
        var first = $"resend-first-{Guid.NewGuid():N}";
        var second = $"resend-second-{Guid.NewGuid():N}";
        using var host = await StartHostAsync(db.AppConnectionString, services =>
            services.AddSingleton<IAlertSender>(sp =>
                new FailOnceSender(new MailKitAlertSender(sp.GetRequiredService<AlertSettings>()), $"{second} is down")));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var healthLog = scope.ServiceProvider.GetRequiredService<IHealthLog>();
            var notifier = scope.ServiceProvider.GetRequiredService<IncidentNotifier>();
            var now = DateTimeOffset.UtcNow;
            var transitions = await healthLog.RecordAsync(
                [new HealthResult(first, ResultStatus.Unhealthy, 0, now, "down"), new HealthResult(second, ResultStatus.Unhealthy, 0, now, "down")],
                Ct);
            transitions.Count.ShouldBe(2);

            await notifier.NotifyPendingAsync(Ct);
        }

        var contexts = host.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>();
        await using (var dbContext = await contexts.CreateDbContextAsync(Ct))
        {
            (await dbContext.Incidents.SingleAsync(i => i.Component == first && i.ClosedAt == null, Ct)).NotifiedOpen.ShouldBeTrue();
            (await dbContext.Incidents.SingleAsync(i => i.Component == second && i.ClosedAt == null, Ct)).NotifiedOpen.ShouldBeFalse();
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IncidentNotifier>();
            await notifier.NotifyPendingAsync(Ct);
            await notifier.NotifyPendingAsync(Ct);
        }

        (await WaitForSubjectAsync($"[WaslaBid] {second} is down", 1)).Count.ShouldBe(1);
        (await MessagesWithSubjectAsync($"[WaslaBid] {first} is down")).Count.ShouldBe(1);
    }

    private async Task<IHost> StartHostAsync(string connectionString, Action<IServiceCollection>? configure = null)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = mailpit.SmtpHost,
            ["Smtp:Port"] = mailpit.SmtpPort.ToString(CultureInfo.InvariantCulture),
            ["Smtp:From"] = "alerts@waslabid.test",
            ["Platform:AlertRecipients:0"] = "platform-admin@waslabid.test",
        });
        builder.Services.AddLogging();
        builder.Services.AddPlatformShared();
        builder.Services.AddOperationsModule(connectionString);
        builder.Services.AddOperationsAlerts(builder.Configuration);
        configure?.Invoke(builder.Services);

        var host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private static HealthCheckJob NewJob(
        IServiceProvider services, IEnumerable<NamedHealthCheck> checks, IHealthLog healthLog, FallbackAlertState fallback) =>
        new(
            checks,
            healthLog,
            services.GetRequiredService<IncidentNotifier>(),
            fallback,
            services.GetRequiredService<IAlertSender>(),
            services.GetRequiredService<AlertSettings>(),
            new HealthTelemetry(services.GetRequiredService<IMeterFactory>(), TimeProvider.System),
            TimeProvider.System,
            NullLogger<HealthCheckJob>.Instance);

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

    private sealed class SwitchableCheck(CheckStatus status, string failureMessage) : IHealthCheck
    {
        public CheckStatus Status { get; set; } = status;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthCheckResult(Status, Status == CheckStatus.Healthy ? null : failureMessage));
    }

    /// <summary>A health log whose store is "down" (throws) until switched back; then it delegates, or records nothing.</summary>
    private sealed class SwitchableHealthLog : IHealthLog
    {
        public const string SecretInMessage = "Password=should-never-appear-in-an-alert";

        public bool Down { get; set; }

        public IHealthLog? Inner { get; set; }

        public Task<IReadOnlyList<IncidentTransition>> RecordAsync(IReadOnlyList<HealthResult> results, CancellationToken cancellationToken = default)
        {
            if (Down)
            {
                throw new InvalidOperationException($"store unavailable; {SecretInMessage}");
            }

            return Inner?.RecordAsync(results, cancellationToken) ?? Task.FromResult<IReadOnlyList<IncidentTransition>>([]);
        }

        public Task<IReadOnlyList<HealthResult>> LatestAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<HealthResult>> LastFailuresAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Incident>> IncidentsAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Fails the first send whose subject contains the marker, then sends normally.</summary>
    private sealed class FailOnceSender(IAlertSender inner, string failSubjectMarker) : IAlertSender
    {
        private bool _failed;

        public Task SendAsync(AlertMessage message, CancellationToken cancellationToken = default)
        {
            if (!_failed && message.Subject.Contains(failSubjectMarker, StringComparison.Ordinal))
            {
                _failed = true;
                throw new InvalidOperationException("Simulated SMTP failure.");
            }

            return inner.SendAsync(message, cancellationToken);
        }
    }
}
