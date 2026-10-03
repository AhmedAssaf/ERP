using System.Globalization;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Operations.Health;
using Platform.Shared;
using Platform.Shared.Jobs;
using CheckStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Platform.IntegrationTests.Operations;

/// <summary>
/// Plan task 3 (F-51): one component check per class, against real dependencies where the plan calls for a
/// Testcontainer (PostgreSQL via <see cref="DatabaseFixture"/>, MinIO, Mailpit) and an in-test double otherwise
/// (ClamAV: a TCP listener; Keycloak and the web host: a minimal Kestrel double, since <see cref="KeycloakFixture"/>
/// does not expose the management port). One theory per plan-listed component, named for that component
/// ("<c>X</c>_reports_healthy/unhealthy_..."), replaces the plan's two generic theories.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HealthChecksTests(DatabaseFixture db, MinioFixture minio, MailpitFixture mailpit)
    : IClassFixture<MinioFixture>, IClassFixture<MailpitFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PostgreSql_reports_healthy_when_up()
    {
        var check = new PostgreSqlHealthCheck(db.AppConnectionString);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        result.Status.ShouldBe(CheckStatus.Healthy);
    }

    [Fact]
    public async Task PostgreSql_reports_unhealthy_with_a_message_naming_the_component_when_down()
    {
        var badConnectionString = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Port = 1 }.ConnectionString;
        var check = new PostgreSqlHealthCheck(badConnectionString);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description.ShouldContain("PostgreSQL");
    }

    [Fact]
    public async Task A_failure_message_never_contains_a_secret()
    {
        const string badPassword = "wrong-password-should-never-appear";
        var badConnectionString = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Password = badPassword }.ConnectionString;

        await using var host = new ModuleHost(db.WorkerConnectionString);
        await using var scope = host.ScopeFor(null);
        var healthLog = scope.ServiceProvider.GetRequiredService<IHealthLog>();

        var check = new PostgreSqlHealthCheck(badConnectionString);
        var checkResult = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        checkResult.Status.ShouldBe(CheckStatus.Unhealthy);

        var component = $"postgres-secret-test-{Guid.NewGuid():N}";
        await healthLog.RecordAsync(
            [new HealthResult(component, Platform.Modules.Operations.Contracts.HealthStatus.Unhealthy, 0, DateTimeOffset.UtcNow, checkResult.Description)],
            Ct);

        var stored = (await healthLog.LatestAsync(Ct)).Single(r => r.Component == component);
        stored.Message.ShouldNotBeNull();
        stored.Message.ShouldNotContain(badPassword);
        stored.Message.ShouldNotContain(badConnectionString);
        stored.Message.ShouldNotContain("Password=", Case.Insensitive);
    }

    [Fact]
    public async Task MinIo_reports_healthy_when_up()
    {
        var check = new MinIoHealthCheck(minio.ServiceUrl, MinioFixture.BucketName, MinioFixture.AccessKey, MinioFixture.SecretKey);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        result.Status.ShouldBe(CheckStatus.Healthy);
    }

    [Fact]
    public async Task MinIo_reports_unhealthy_with_a_message_naming_the_component_when_down()
    {
        var check = new MinIoHealthCheck(minio.ServiceUrl, "no-such-bucket-for-this-test", MinioFixture.AccessKey, MinioFixture.SecretKey);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description.ShouldContain("MinIO");
    }

    [Fact]
    public async Task Keycloak_reports_healthy_when_up()
    {
        await using var fake = await FakeHttpServer.StartAsync(
            context => WriteJsonAsync(context, 200, """{"status":"UP"}"""), Ct);

        using var httpClient = new HttpClient();
        var check = new KeycloakHealthCheck(httpClient, fake.BaseAddress);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        result.Status.ShouldBe(CheckStatus.Healthy);
    }

    [Fact]
    public async Task Keycloak_reports_unhealthy_with_a_message_naming_the_component_when_not_ready()
    {
        await using var fake = await FakeHttpServer.StartAsync(
            context => WriteJsonAsync(context, 503, """{"status":"DOWN"}"""), Ct);

        using var httpClient = new HttpClient();
        var check = new KeycloakHealthCheck(httpClient, fake.BaseAddress);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description.ShouldContain("Keycloak");
    }

    [Fact]
    public async Task ClamAv_reports_healthy_when_up()
    {
        await using var fake = FakePingServer.StartResponding();
        var check = new ClamAvHealthCheck("127.0.0.1", fake.Port);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        result.Status.ShouldBe(CheckStatus.Healthy);
    }

    [Fact]
    public async Task ClamAv_reports_unhealthy_with_a_message_naming_the_component_when_it_does_not_respond()
    {
        await using var fake = FakePingServer.StartSilent();
        using var shortTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Ct, shortTimeout.Token);

        var check = new ClamAvHealthCheck("127.0.0.1", fake.Port);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), linked.Token);

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description.ShouldContain("ClamAV");
    }

    [Fact]
    public async Task Smtp_reports_healthy_when_up()
    {
        var check = new SmtpHealthCheck(mailpit.SmtpHost, mailpit.SmtpPort);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        result.Status.ShouldBe(CheckStatus.Healthy);
    }

    [Fact]
    public async Task Smtp_reports_unhealthy_with_a_message_naming_the_component_when_down()
    {
        var check = new SmtpHealthCheck("127.0.0.1", 1);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description.ShouldContain("SMTP");
    }

    [Fact]
    public async Task Worker_reports_healthy_when_the_heartbeat_is_recent()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        await WaitUntilAsync(worker.ServerIsRegistered);

        var check = new WorkerHeartbeatHealthCheck(worker.Storage, TimeProvider.System);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        result.Status.ShouldBe(CheckStatus.Healthy);
    }

    [Fact]
    public async Task Worker_reports_unhealthy_with_a_message_naming_the_component_when_there_is_no_heartbeat()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        await WaitUntilAsync(worker.ServerIsRegistered);

        // The server's heartbeat is real and recent; move the check's clock far enough ahead that it reads stale.
        var check = new WorkerHeartbeatHealthCheck(worker.Storage, new OffsetTimeProvider(TimeSpan.FromMinutes(5)));
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description.ShouldContain("Worker");
    }

    [Fact]
    public async Task Web_reports_healthy_when_the_endpoint_is_up()
    {
        await using var fake = await FakeHttpServer.StartAsync(context => WriteTextAsync(context, 200, "Healthy"), Ct);
        using var httpClient = new HttpClient();
        var check = new WebHealthCheck(httpClient, fake.BaseAddress + "health");
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        result.Status.ShouldBe(CheckStatus.Healthy);
    }

    [Fact]
    public async Task Web_reports_unhealthy_with_a_message_naming_the_component_when_the_endpoint_is_down()
    {
        await using var fake = await FakeHttpServer.StartAsync(context => WriteTextAsync(context, 503, "Unhealthy"), Ct);
        using var httpClient = new HttpClient();
        var check = new WebHealthCheck(httpClient, fake.BaseAddress + "health");
        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(CheckStatus.Unhealthy);
        result.Description.ShouldNotBeNull();
        result.Description.ShouldContain("Web");
    }

    [Fact]
    public async Task The_check_job_records_one_result_per_component()
    {
        await using var keycloakFake = await FakeHttpServer.StartAsync(
            context => WriteJsonAsync(context, 200, """{"status":"UP"}"""), Ct);
        await using var webFake = await FakeHttpServer.StartAsync(context => WriteTextAsync(context, 200, "Healthy"), Ct);
        await using var clamAvFake = FakePingServer.StartResponding();

        var settings = new Dictionary<string, string?>
        {
            ["Health:MinIo:ServiceUrl"] = minio.ServiceUrl,
            ["Health:MinIo:BucketName"] = MinioFixture.BucketName,
            ["Health:MinIo:AccessKey"] = MinioFixture.AccessKey,
            ["Health:MinIo:SecretKey"] = MinioFixture.SecretKey,
            ["Keycloak:ManagementUrl"] = keycloakFake.BaseAddress,
            ["ClamAv:Host"] = "127.0.0.1",
            ["ClamAv:Port"] = clamAvFake.Port.ToString(CultureInfo.InvariantCulture),
            ["Smtp:Host"] = mailpit.SmtpHost,
            ["Smtp:Port"] = mailpit.SmtpPort.ToString(CultureInfo.InvariantCulture),
            ["Platform:WebHealthUrl"] = webFake.BaseAddress + "health",
        };

        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.Services.AddPlatformShared();
        builder.Services.AddOperationsModule(db.WorkerConnectionString);
        builder.Services.AddOperationsHealthChecks(db.WorkerConnectionString, builder.Configuration);
        builder.Services.AddJobServer(db.WorkerConnectionString, TestSecrets.JobKeys, options => options.ServerName = $"health-job-test-{Guid.NewGuid():N}");

        using var host = builder.Build();
        await host.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(() => host.Services.GetRequiredService<JobStorage>()
                .GetMonitoringApi().Servers().Count > 0);

            await using var scope = host.Services.CreateAsyncScope();
            var job = scope.ServiceProvider.GetRequiredService<HealthCheckJob>();
            await job.RunAsync(Ct);

            var healthLog = scope.ServiceProvider.GetRequiredService<IHealthLog>();
            var results = await healthLog.LatestAsync(Ct);

            // ops.health_results is platform-wide (no tenant_id) and shared with other tests against the same
            // database fixture, so assert each configured check produced exactly one row rather than an exact set.
            string[] expectedComponents = ["PostgreSQL", "MinIO", "Keycloak", "ClamAV", "SMTP", "Worker", "Web"];
            foreach (var component in expectedComponents)
            {
                results.Count(r => r.Component == component).ShouldBe(1, $"expected exactly one result for {component}");
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body);
    }

    private static async Task WriteTextAsync(HttpContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsync(body);
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

    private sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}
