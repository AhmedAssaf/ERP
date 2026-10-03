using System.Diagnostics;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using MetricPoint = OpenTelemetry.Metrics.MetricPoint;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations;
using Platform.Modules.Operations.Health;
using Platform.Shared.Jobs;
using Platform.Shared.Telemetry;
using CheckStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Platform.IntegrationTests.Telemetry;

/// <summary>
/// W-10, plan task 4 (spec 5.2, 5.4): the worker's health job publishes each result on meter <c>WaslaBid.Operations</c>, the
/// status as a gauge (0 Healthy, 1 Degraded, 2 Unhealthy) and the check's duration as a histogram, both tagged with the
/// component under <c>waslabid.component</c>; the run is one span with a child per check, below the job's own span.
/// </summary>
/// <remarks>
/// Meter providers listen by meter name and activity listeners process-wide, so another host's health points or spans can
/// appear: each test checks components named for it alone and finds its spans by those names.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class HealthTelemetryTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_health_result_is_published_as_a_status_and_latency_metric()
    {
        var telemetry = new CapturedTelemetry();
        var components = UniqueComponents();
        await using var worker = await StartWorkerAsync(telemetry, components);

        await RunHealthJobAsync(worker);

        var metrics = telemetry.CollectMetrics(worker.Services);
        foreach (var (component, status, expected) in components)
        {
            var statusPoints = Points(metrics, TelemetryNames.Metrics.HealthStatus, component);
            statusPoints.Count.ShouldBe(1, $"one status point for {component} ({status})");
            statusPoints[0].GetGaugeLastValueLong().ShouldBe(expected);

            var durationPoints = Points(metrics, TelemetryNames.Metrics.HealthCheckDuration, component);
            durationPoints.Count.ShouldBe(1, $"one duration point for {component}");
            durationPoints[0].GetHistogramCount().ShouldBe(1);
            durationPoints[0].GetHistogramSum().ShouldBeGreaterThanOrEqualTo(0);
        }

        metrics.Where(m => m.Name == TelemetryNames.Metrics.HealthStatus).ShouldAllBe(m => m.MeterName == TelemetryNames.Sources.Operations);
        metrics.Where(m => m.Name == TelemetryNames.Metrics.HealthCheckDuration).ShouldAllBe(m => m.MeterName == TelemetryNames.Sources.Operations && m.Unit == "ms");
    }

    [Fact]
    public async Task A_health_run_is_one_span_with_a_child_per_check_below_the_job_span()
    {
        var telemetry = new CapturedTelemetry();
        var components = UniqueComponents();
        await using var worker = await StartWorkerAsync(telemetry, components);

        var jobId = await RunHealthJobAsync(worker);

        var checkSpans = await WaitForCheckSpansAsync(telemetry, components.Select(c => c.Component).ToList());
        var run = checkSpans.Select(s => s.Parent).Distinct().ShouldHaveSingleItem();
        run.ShouldNotBeNull();
        run.Source.Name.ShouldBe(TelemetryNames.Sources.Operations);
        run.OperationName.ShouldBe("health check run");

        var job = run.Parent;
        job.ShouldNotBeNull("the run is a child of the job's span");
        job.Source.Name.ShouldBe(TelemetryNames.Sources.Jobs);
        job.GetTagItem(TelemetryNames.Attributes.JobId).ShouldBe(jobId);

        foreach (var (component, status, _) in components)
        {
            var span = checkSpans.Single(s => Equals(s.GetTagItem(TelemetryNames.Attributes.Component), component));
            span.Source.Name.ShouldBe(TelemetryNames.Sources.Operations);
            span.Status.ShouldBe(status == CheckStatus.Unhealthy ? ActivityStatusCode.Error : ActivityStatusCode.Unset);
        }
    }

    private static List<(string Component, CheckStatus Status, long Expected)> UniqueComponents()
    {
        var prefix = $"health-metric-{Guid.NewGuid():N}";
        return
        [
            ($"{prefix}-healthy", CheckStatus.Healthy, 0),
            ($"{prefix}-degraded", CheckStatus.Degraded, 1),
            ($"{prefix}-unhealthy", CheckStatus.Unhealthy, 2),
        ];
    }

    private async Task<JobServerHost> StartWorkerAsync(
        CapturedTelemetry telemetry, IEnumerable<(string Component, CheckStatus Status, long Expected)> components)
    {
        // No alert recipients: the unhealthy component's incident sends nothing.
        var configuration = new ConfigurationBuilder().Build();
        return await JobServerHost.StartAsync(
            db.WorkerConnectionString,
            services =>
            {
                telemetry.AddTo(services);
                services.AddOperationsModule(db.AppConnectionString);
                services.AddOperationsAlerts(configuration);
                foreach (var (component, status, _) in components)
                {
                    services.AddSingleton(new NamedHealthCheck(component, new FixedCheck(status)));
                }

                services.AddHealthCheckJob();
            },
            configureHost: builder => builder.AddPlatformTelemetry(TelemetryNames.Services.Worker),
            cancellationToken: Ct);
    }

    private static async Task<string> RunHealthJobAsync(JobServerHost worker)
    {
        string jobId;
        await using (var scope = worker.ScopeFor(null))
        {
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>()
                .Enqueue<HealthCheckJob>(job => job.RunAsync(CancellationToken.None));
        }

        await worker.WaitForSuccessAsync(jobId, Ct);
        return jobId;
    }

    private static async Task<List<Activity>> WaitForCheckSpansAsync(CapturedTelemetry telemetry, List<string> components)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var spans = telemetry.AllSpans
                .Where(s => s.GetTagItem(TelemetryNames.Attributes.Component) is string c && components.Contains(c))
                .ToList();
            if ((spans.Count >= components.Count && spans.All(s => s.Parent is { Parent: not null })) || DateTime.UtcNow > deadline)
            {
                spans.Count.ShouldBe(components.Count, "one span per check");
                return spans;
            }

            await Task.Delay(25, Ct);
        }
    }

    private static List<MetricPoint> Points(IReadOnlyList<MetricSnapshot> metrics, string name, string component) =>
        [.. metrics.Where(m => m.Name == name).SelectMany(m => m.MetricPoints).Where(p => HasComponent(p, component))];

    private static bool HasComponent(MetricPoint point, string component)
    {
        foreach (var tag in point.Tags)
        {
            // The same key as the logs' component (W-10 ruling): waslabid.component, never a bare "component".
            if (tag.Key == TelemetryNames.Attributes.Component && Equals(tag.Value, component))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FixedCheck(CheckStatus status) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthCheckResult(status, status == CheckStatus.Healthy ? null : "Fixed test result."));
    }
}
