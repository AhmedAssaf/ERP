using System.Globalization;
using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations;
using Platform.Modules.Operations.Alerts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Operations.Health;
using Platform.Shared;

namespace Platform.IntegrationTests.Operations;

/// <summary>
/// Plan task 4 (F-60 as narrowed): alert emails, asserted against Mailpit's HTTP API (docs/07 section 4: Mailpit
/// catches everything the app sends, nothing leaves the Compose network).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AlertsTests(DatabaseFixture db, MailpitFixture mailpit) : IClassFixture<MailpitFixture>, IAsyncLifetime
{
    private const string Recipient = "platform-admin@waslabid.test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(AlertConfiguration());
        builder.Services.AddLogging();
        builder.Services.AddPlatformShared();
        builder.Services.AddOperationsModule(db.AppConnectionString);
        builder.Services.AddOperationsAlerts(builder.Configuration);

        _host = builder.Build();
        await _host.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }

    [Fact]
    public async Task An_incident_sends_one_email_and_no_repeat_while_open()
    {
        var component = UniqueComponent();
        await using var scope = _host.Services.CreateAsyncScope();
        var healthLog = scope.ServiceProvider.GetRequiredService<IHealthLog>();
        var notifier = scope.ServiceProvider.GetRequiredService<IncidentNotifier>();
        var now = DateTimeOffset.UtcNow;

        await notifier.NotifyAsync(await healthLog.RecordAsync([Failure(component, now)], Ct), Ct);
        // A repeated failure while the incident is still open must not send a second email.
        await notifier.NotifyAsync(await healthLog.RecordAsync([Failure(component, now.AddSeconds(1))], Ct), Ct);

        var messages = await WaitForMessagesAsync(component, expectedCount: 1);
        messages[0].GetProperty("Subject").GetString().ShouldBe($"[WaslaBid] {component} is down");
        messages[0].GetProperty("To").EnumerateArray().Single().GetProperty("Address").GetString().ShouldBe(Recipient);
    }

    [Fact]
    public async Task Recovery_sends_one_notice()
    {
        var component = UniqueComponent();
        await using var scope = _host.Services.CreateAsyncScope();
        var healthLog = scope.ServiceProvider.GetRequiredService<IHealthLog>();
        var notifier = scope.ServiceProvider.GetRequiredService<IncidentNotifier>();
        var now = DateTimeOffset.UtcNow;

        await notifier.NotifyAsync(await healthLog.RecordAsync([Failure(component, now)], Ct), Ct);
        await notifier.NotifyAsync(await healthLog.RecordAsync([Healthy(component, now.AddSeconds(1))], Ct), Ct);

        var messages = await WaitForMessagesAsync(component, expectedCount: 2);
        var subjects = messages.Select(m => m.GetProperty("Subject").GetString()).ToList();
        subjects.ShouldContain($"[WaslaBid] {component} is down");
        subjects.ShouldContain($"[WaslaBid] {component} has recovered");
    }

    [Fact]
    public async Task An_alert_contains_no_secret_value()
    {
        const string badPassword = "wrong-password-should-never-appear";
        var badConnectionString = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Password = badPassword }.ConnectionString;
        var component = UniqueComponent();

        var check = new PostgreSqlHealthCheck(badConnectionString);
        var checkResult = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext(), Ct);
        checkResult.Status.ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);

        await using var scope = _host.Services.CreateAsyncScope();
        var healthLog = scope.ServiceProvider.GetRequiredService<IHealthLog>();
        var notifier = scope.ServiceProvider.GetRequiredService<IncidentNotifier>();

        var transitions = await healthLog.RecordAsync(
            [new HealthResult(component, HealthStatus.Unhealthy, 0, DateTimeOffset.UtcNow, checkResult.Description)], Ct);
        await notifier.NotifyAsync(transitions, Ct);

        var messages = await WaitForMessagesAsync(component, expectedCount: 1);
        var full = await FetchMessageAsync(messages[0].GetProperty("ID").GetString()!);
        var text = full.GetProperty("Text").GetString() ?? string.Empty;

        text.ShouldNotContain(badPassword);
        text.ShouldNotContain(badConnectionString);
        text.ShouldNotContain("Password=", Case.Insensitive);
    }

    [Fact]
    public async Task A_job_failing_three_times_sends_one_alert()
    {
        await using var worker = await JobServerHost.StartAsync(
            db.AppConnectionString,
            services =>
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(AlertConfiguration()).Build();
                services.AddOperationsAlerts(configuration);
            },
            configureJobServer: options => options.SchedulePollingInterval = TimeSpan.FromMilliseconds(250),
            cancellationToken: Ct);

        await WaitUntilAsync(worker.ServerIsRegistered);

        string jobId;
        await using (var scope = worker.ScopeFor(null))
        {
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>()
                .Enqueue<AlwaysFailingTestJob>(job => job.Run());
        }

        var messages = await WaitForMessagesContainingAsync($"Job id: {jobId}", expectedCount: 1, timeoutSeconds: 90);
        var subject = messages[0].GetProperty("Subject").GetString();
        subject.ShouldNotBeNull();
        subject.ShouldContain("failed three times in a row");
    }

    private Dictionary<string, string?> AlertConfiguration() => new()
    {
        ["Smtp:Host"] = mailpit.SmtpHost,
        ["Smtp:Port"] = mailpit.SmtpPort.ToString(CultureInfo.InvariantCulture),
        ["Smtp:From"] = "alerts@waslabid.test",
        ["Platform:AlertRecipients:0"] = Recipient,
    };

    private static string UniqueComponent() => $"test-component-{Guid.NewGuid():N}";

    private static HealthResult Failure(string component, DateTimeOffset at) =>
        new(component, HealthStatus.Unhealthy, 0, at, "connection refused");

    private static HealthResult Healthy(string component, DateTimeOffset at) =>
        new(component, HealthStatus.Healthy, 5, at);

    private async Task<List<JsonElement>> WaitForMessagesAsync(string subjectMarker, int expectedCount, int timeoutSeconds = 30) =>
        await WaitForMessagesMatchingAsync(m => (m.GetProperty("Subject").GetString() ?? string.Empty).Contains(subjectMarker, StringComparison.Ordinal), expectedCount, timeoutSeconds);

    private async Task<List<JsonElement>> WaitForMessagesContainingAsync(string bodyMarker, int expectedCount, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var candidates = await ListMessagesAsync();
            var matches = new List<JsonElement>();
            foreach (var candidate in candidates)
            {
                var full = await FetchMessageAsync(candidate.GetProperty("ID").GetString()!);
                if ((full.GetProperty("Text").GetString() ?? string.Empty).Contains(bodyMarker, StringComparison.Ordinal))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count >= expectedCount)
            {
                matches.Count.ShouldBe(expectedCount, "expected exactly one alert for this job id");
                return matches;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {expectedCount} message(s) containing '{bodyMarker}' within {timeoutSeconds}s, found {matches.Count}.");
            }

            await Task.Delay(200, Ct);
        }
    }

    private async Task<List<JsonElement>> WaitForMessagesMatchingAsync(Func<JsonElement, bool> predicate, int expectedCount, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var matches = (await ListMessagesAsync()).Where(predicate).ToList();
            if (matches.Count >= expectedCount)
            {
                matches.Count.ShouldBe(expectedCount);
                return matches;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {expectedCount} message(s) within {timeoutSeconds}s, found {matches.Count}.");
            }

            await Task.Delay(200, Ct);
        }
    }

    private async Task<List<JsonElement>> ListMessagesAsync()
    {
        using var httpClient = new HttpClient { BaseAddress = mailpit.ApiBaseAddress };
        using var response = await httpClient.GetAsync("/api/v1/messages?limit=250", Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone())];
    }

    private async Task<JsonElement> FetchMessageAsync(string id)
    {
        using var httpClient = new HttpClient { BaseAddress = mailpit.ApiBaseAddress };
        using var response = await httpClient.GetAsync($"/api/v1/message/{id}", Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.Clone();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true within 30 seconds.");
            }

            await Task.Delay(100, Ct);
        }
    }
}

/// <summary>A job that always fails, with near-zero retry delays so the third attempt (plan task 4's alert
/// trigger) arrives quickly instead of waiting out Hangfire's real back-off schedule.</summary>
public sealed class AlwaysFailingTestJob
{
#pragma warning disable CA1822 // Instance method by convention: Hangfire jobs are activated per execution.
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [0, 0, 0, 0, 0])]
    public void Run() => throw new InvalidOperationException("Intentional failure for the job-failure-alert test.");
#pragma warning restore CA1822
}
