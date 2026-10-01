using System.Diagnostics;
using System.Diagnostics.Metrics;
using Hangfire.Common;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Platform.Shared.Telemetry;

namespace Platform.Shared.Jobs;

/// <summary>
/// A span per Hangfire job (W-10, spec 5.2, 5.4, O-9), registered per job server (<see cref="JobsModule.AddJobServer"/>). While
/// the job performs: activity <c>job Type.Method</c> from source <see cref="TelemetryNames.Sources.Jobs"/>, a child of the span
/// that enqueued it (the <see cref="TenantJobFilter.TraceParentParameter"/> parameter) or the root of a new trace (a recurring
/// job, or one enqueued outside any request), tagged with the job id, the job type and the tenant id; and a log scope with the
/// same three, so every record the job writes carries them. A failed job's span gets status Error and <c>exception.type</c>;
/// <see cref="TelemetryNames.Metrics.JobsDuration"/> and <see cref="TelemetryNames.Metrics.JobsFailed"/> are recorded per job
/// type only. The job's arguments are never read, and the exception's message and stack trace never reach the span (spec 7:
/// they leave only through the masked log record).
/// </summary>
/// <remarks>
/// Hangfire calls <see cref="OnPerforming"/>, the job and <see cref="OnPerformed"/> one after the other on the worker's thread,
/// so the activity and the scope set here are current while the job runs. A worker thread runs many jobs: the activity that
/// was current before is put back afterwards, and a job without a parent starts from no current activity at all, so nothing
/// of one job's trace can leak into the next.
/// </remarks>
public sealed class JobTelemetryFilter : IServerFilter, IJobFilter
{
    /// <summary>The activity source of the job spans; every <c>WaslaBid.*</c> source is registered by both hosts.</summary>
    public static readonly ActivitySource Source = new(TelemetryNames.Sources.Jobs);

    private static readonly string StateItem = typeof(JobTelemetryFilter).FullName!;

    /// <summary>Seconds, from a quick job to the ten minutes of a long one; the SDK's default buckets are made for milliseconds.</summary>
    private static readonly double[] DurationBuckets = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600];

    private readonly ILogger<JobTelemetryFilter> _logger;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _failed;

    public JobTelemetryFilter(ILogger<JobTelemetryFilter> logger, IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _logger = logger;
        // The meter belongs to the factory, which disposes it with the host.
        var meter = meters.Create(TelemetryNames.Sources.Jobs);
        _duration = meter.CreateHistogram(
            TelemetryNames.Metrics.JobsDuration,
            "s",
            "How long each Hangfire job took to perform, per job type.",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
        _failed = meter.CreateCounter<long>(TelemetryNames.Metrics.JobsFailed, "{job}", "Hangfire job runs that failed, per job type.");
    }

    /// <summary>Before every other server filter, so the span and the scope cover them (Hangfire's own default order is -1).</summary>
    public int Order => -1000;

    public bool AllowMultiple => false;

    /// <summary>The job's <c>Type.Method</c>, never its arguments (N-10), as the job-failure alert names it.</summary>
    public static string JobType(Job? job) => job is null ? "(unknown job)" : $"{job.Type.Name}.{job.Method.Name}";

    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var jobType = JobType(context.BackgroundJob.Job);
        // The parameters are written once, when the job is created: the copy fetched with the job needs no second read.
        var tenantId = context.GetJobParameter<Guid?>(TenantJobFilter.TenantIdParameter, allowStale: true);
        List<KeyValuePair<string, object?>> tags =
        [
            new(TelemetryNames.Attributes.JobId, context.BackgroundJob.Id),
            new(TelemetryNames.Attributes.JobType, jobType),
        ];
        if (tenantId is { } tenant)
        {
            tags.Add(new(TelemetryNames.Attributes.TenantId, tenant.ToString()));
        }

        var previous = Activity.Current;
        var parent = ParentOf(context.GetJobParameter<string>(TenantJobFilter.TraceParentParameter, allowStale: true));
        // Never a child of whatever was current on the worker's thread: of the enqueuing span, or the root of its own trace.
        Activity.Current = null;
        var activity = Source.StartActivity($"job {jobType}", ActivityKind.Internal, parent, tags);
        var scope = _logger.BeginScope(tags);
        context.Items[StateItem] = new State(activity, scope, previous, jobType, Stopwatch.GetTimestamp());
    }

    public void OnPerformed(PerformedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Items.TryGetValue(StateItem, out var item) || item is not State state)
        {
            return;
        }

        context.Items.Remove(StateItem);

        var jobTag = new KeyValuePair<string, object?>(TelemetryNames.MetricTags.JobType, state.JobType);
        try
        {
            if (context.Exception is { } exception && !context.ExceptionHandled)
            {
                // Hangfire wraps what the job threw; the job's own exception names the failure.
                var thrown = exception is JobPerformanceException { InnerException: { } inner } ? inner : exception;
                state.Activity?.SetTag(TelemetryNames.Attributes.ExceptionType, thrown.GetType().FullName);
                state.Activity?.SetStatus(ActivityStatusCode.Error);
                _failed.Add(1, jobTag);
            }

            _duration.Record(Stopwatch.GetElapsedTime(state.StartedAt).TotalSeconds, jobTag);
        }
        finally
        {
            state.Scope?.Dispose();
            state.Activity?.Dispose();
            Activity.Current = state.Previous;
        }
    }

    /// <summary>The enqueuing span's context, or none (a new trace) when the parameter is missing or not a W3C id.</summary>
    private static ActivityContext ParentOf(string? traceParent) =>
        ActivityContext.TryParse(traceParent, traceState: null, isRemote: true, out var parent) ? parent : default;

    private sealed record State(Activity? Activity, IDisposable? Scope, Activity? Previous, string JobType, long StartedAt);
}
