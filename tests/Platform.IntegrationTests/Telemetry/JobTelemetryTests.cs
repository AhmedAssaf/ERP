using System.Diagnostics;
using System.Net;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using MetricPoint = OpenTelemetry.Metrics.MetricPoint;
using Platform.IntegrationTests.Infrastructure;
using Platform.Shared.Telemetry;

namespace Platform.IntegrationTests.Telemetry;

/// <summary>
/// W-10, plan task 2 (spec 5.2, 5.4, O-9): a span per Hangfire job, a child of the request that enqueued it or the root of a
/// trace of its own; the job's context on every log record it writes; an Error status and the job metrics on failure; never
/// the job's arguments.
/// </summary>
/// <remarks>
/// The job spans come from a static activity source that every tracer provider in the process listens to, so each test
/// finds its spans by the job id it enqueued, never by position.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class JobTelemetryTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_job_enqueued_during_a_request_continues_that_requests_trace()
    {
        var web = new CapturedTelemetry();
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            web.AddTo(services);
            services.AddSingleton<IStartupFilter, EnqueueEndpoint>();
        }));
        var worker = new CapturedTelemetry();
        await using var host = await StartWorkerAsync(worker);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });
        var marker = Guid.NewGuid().ToString("N");

        using var response = await client.GetAsync(new Uri($"{EnqueueEndpoint.Path}?marker={marker}", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var jobId = await response.Content.ReadAsStringAsync(Ct);
        await host.WaitForSuccessAsync(jobId, Ct);

        var request = (await web.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem();
        var job = await JobSpanAsync(worker, jobId);
        job.TraceId.ShouldBe(request.TraceId);
        job.ParentSpanId.ShouldBe(request.SpanId);
        job.HasRemoteParent.ShouldBeTrue();
        job.DisplayName.ShouldBe($"job {nameof(TelemetryProbeJob)}.{nameof(TelemetryProbeJob.Run)}");
        job.GetTagItem(TelemetryNames.Attributes.TenantId).ShouldBe(TestTenants.Acme.TenantId.ToString());
        Record(worker, marker).TraceId.ShouldBe(request.TraceId, "the job's log records belong to the request's trace too");
    }

    [Fact]
    public async Task A_log_written_inside_a_job_carries_the_job_id_type_and_tenant_id()
    {
        var telemetry = new CapturedTelemetry();
        await using var worker = await StartWorkerAsync(telemetry);
        var marker = Guid.NewGuid().ToString("N");

        string jobId;
        await using (var scope = worker.ScopeFor(TestTenants.Acme))
        {
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>().Enqueue<TelemetryProbeJob>(job => job.Run(marker));
        }

        await worker.WaitForSuccessAsync(jobId, Ct);

        var record = Record(telemetry, marker);
        record.Properties[TelemetryNames.Attributes.JobId].ShouldBe(jobId);
        record.Properties[TelemetryNames.Attributes.JobType].ShouldBe("TelemetryProbeJob.Run");
        record.Properties[TelemetryNames.Attributes.TenantId].ShouldBe(TestTenants.Acme.TenantId.ToString());
        var span = await JobSpanAsync(telemetry, jobId);
        record.TraceId.ShouldBe(span.TraceId);
        record.SpanId.ShouldBe(span.SpanId);
        span.GetTagItem(TelemetryNames.Attributes.JobType).ShouldBe("TelemetryProbeJob.Run");
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task A_failed_job_marks_its_span_as_an_error_with_the_exception_type_and_no_arguments()
    {
        const string secret = "fake-secret-job-argument-4c1d";
        var telemetry = new CapturedTelemetry();
        await using var worker = await StartWorkerAsync(telemetry);

        string jobId;
        await using (var scope = worker.ScopeFor(TestTenants.Acme))
        {
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>().Enqueue<FailingTelemetryJob>(job => job.Run(secret));
        }

        await WaitForStateAsync(worker, jobId, "Failed");

        var span = await JobSpanAsync(telemetry, jobId);
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem(TelemetryNames.Attributes.ExceptionType).ShouldBe(typeof(InvalidOperationException).FullName);
        span.StatusDescription.ShouldBeNull("the exception's message never leaves on the span");
        span.TagObjects.ShouldAllBe(tag => tag.Value == null || !tag.Value.ToString()!.Contains(secret, StringComparison.Ordinal));
        span.Events.ShouldBeEmpty("no exception event with the message and stack trace");
        span.DisplayName.ShouldNotContain(secret);
        telemetry.Logs.Where(l => l.Properties.GetValueOrDefault(TelemetryNames.Attributes.JobId) == jobId)
            .ShouldAllBe(l => !l.Message.Contains(secret, StringComparison.Ordinal) && l.Properties.Values.All(v => v == null || !v.Contains(secret, StringComparison.Ordinal)));

        var metrics = telemetry.CollectMetrics(worker.Services);
        Points(metrics, TelemetryNames.Metrics.JobsFailed, "FailingTelemetryJob.Run").Sum(p => p.GetSumLong()).ShouldBe(1);
        Points(metrics, TelemetryNames.Metrics.JobsDuration, "FailingTelemetryJob.Run").Sum(p => p.GetHistogramCount()).ShouldBe(1);
        metrics.Where(m => m.Name is TelemetryNames.Metrics.JobsFailed or TelemetryNames.Metrics.JobsDuration)
            .SelectMany(m => m.MetricPoints)
            .SelectMany(Tags)
            .Select(t => t.Key)
            .Distinct()
            .ShouldBe([TelemetryNames.MetricTags.JobType], "no tenant, job id or argument on a technical metric");
    }

    [Fact]
    public async Task A_recurring_job_without_a_request_starts_its_own_trace()
    {
        using var ambientSource = new ActivitySource("WaslaBid.Tests.Ambient");
        var telemetry = new CapturedTelemetry();
        await using var worker = await StartWorkerAsync(telemetry);
        var recurringId = $"telemetry-recurring-{Guid.NewGuid():N}";
        var marker = Guid.NewGuid().ToString("N");
        var manager = new RecurringJobManager(worker.Storage);
        manager.AddOrUpdate<TelemetryProbeJob>(recurringId, job => job.Run(marker), Cron.Never());

        string jobId;
        ActivityTraceId ambientTrace;
        using (var ambient = ambientSource.StartActivity("test ambient"))
        {
            ambient.ShouldNotBeNull("the worker's tracer provider listens to every WaslaBid.* source");
            ambientTrace = ambient.TraceId;
            jobId = manager.TriggerJob(recurringId);
        }

        await worker.WaitForSuccessAsync(jobId, Ct);
        manager.RemoveIfExists(recurringId);

        worker.Storage.GetParameter(jobId, "TraceParent").ShouldBeNull();
        var span = await JobSpanAsync(telemetry, jobId);
        span.ParentSpanId.ShouldBe(default);
        span.TraceId.ShouldNotBe(ambientTrace);
        Record(telemetry, marker).TraceId.ShouldBe(span.TraceId);
    }

    private async Task<JobServerHost> StartWorkerAsync(CapturedTelemetry telemetry)
    {
        var worker = await JobServerHost.StartAsync(
            db.AppConnectionString,
            telemetry.AddTo,
            configureHost: builder => builder.AddPlatformTelemetry(TelemetryNames.Services.Worker),
            cancellationToken: Ct);
        return worker;
    }

    private static async Task<Activity> JobSpanAsync(CapturedTelemetry telemetry, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var spans = telemetry.AllSpans.Where(s => s.Source.Name == TelemetryNames.Sources.Jobs && (s.GetTagItem(TelemetryNames.Attributes.JobId) as string) == jobId).ToList();
            if (spans.Count > 0 || DateTime.UtcNow > deadline)
            {
                return spans.ShouldHaveSingleItem($"one span for job {jobId}");
            }

            await Task.Delay(25, Ct);
        }
    }

    private static CapturedLog Record(CapturedTelemetry telemetry, string marker) =>
        telemetry.Logs.Where(l => l.Template == TelemetryProbeJob.Template && l.Properties.GetValueOrDefault("Marker") == marker).ShouldHaveSingleItem();

    private static List<MetricPoint> Points(IReadOnlyList<MetricSnapshot> metrics, string name, string jobType) =>
        [.. metrics.Where(m => m.Name == name).SelectMany(m => m.MetricPoints).Where(p => Tags(p).Any(t => t.Key == TelemetryNames.MetricTags.JobType && Equals(t.Value, jobType)))];

    private static List<KeyValuePair<string, object?>> Tags(MetricPoint point)
    {
        var tags = new List<KeyValuePair<string, object?>>();
        foreach (var tag in point.Tags)
        {
            tags.Add(tag);
        }

        return tags;
    }

    private static async Task WaitForStateAsync(JobServerHost worker, string jobId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            string? state;
            using (var connection = worker.Storage.GetConnection())
            {
                state = connection.GetStateData(jobId)?.Name;
            }

            if (state == expected)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Job {jobId} did not reach {expected} (last state {state}).");
            }

            await Task.Delay(100, Ct);
        }
    }

    /// <summary>An anonymous tenant endpoint that enqueues <see cref="TelemetryProbeJob"/> and answers with the job id.</summary>
    private sealed class EnqueueEndpoint : IStartupFilter
    {
        public const string Path = "/test/telemetry/enqueue";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = app.Properties.TryGetValue("__EndpointRouteBuilder", out var value) && value is IEndpointRouteBuilder routeBuilder
                ? routeBuilder
                : throw new InvalidOperationException("The host did not expose its endpoint route builder.");
            endpoints.MapGet(Path, (string marker, IBackgroundJobClient jobs) => Results.Text(jobs.Enqueue<TelemetryProbeJob>(job => job.Run(marker))))
                .AllowAnonymous();
        };
    }
}

/// <summary>Writes one log record with a marker (W-10 job telemetry tests).</summary>
public sealed partial class TelemetryProbeJob(ILogger<TelemetryProbeJob> logger)
{
    public const string Template = "Telemetry job probe {Marker}";

    public void Run(string marker) => Logged(logger, marker);

    [LoggerMessage(Level = LogLevel.Information, Message = Template)]
    private static partial void Logged(ILogger logger, string marker);
}

/// <summary>Fails at once, without retries, with an argument that must never reach the telemetry.</summary>
[AutomaticRetry(Attempts = 0)]
public sealed class FailingTelemetryJob
{
#pragma warning disable CA1822 // Instance method by convention: Hangfire jobs are activated per execution.
    public void Run(string secret) => throw new InvalidOperationException($"Deliberate job failure: {secret.Length} characters.");
#pragma warning restore CA1822
}
