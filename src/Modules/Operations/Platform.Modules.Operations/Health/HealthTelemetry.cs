using System.Diagnostics;
using System.Diagnostics.Metrics;
using Platform.Modules.Operations.Contracts;
using Platform.Shared.Telemetry;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// The health job's telemetry (W-10, spec 5.2, 5.4): the run is one span from <see cref="TelemetryNames.Sources.Operations"/>
/// with a child per check, and each result is published on the meter of the same name as
/// <see cref="TelemetryNames.Metrics.HealthStatus"/> (observable gauge: 0 Healthy, 1 Degraded, 2 Unhealthy) and
/// <see cref="TelemetryNames.Metrics.HealthCheckDuration"/> (histogram, milliseconds), both tagged with the component only
/// (<see cref="TelemetryNames.MetricTags.Component"/>). Published from the job's own memory before the results are stored, so
/// a PostgreSQL outage, which is when the store fails, still shows in the metrics. The gauge reports the last run while it is
/// fresh (<see cref="HealthComponents.StaleAfter"/>), so a stopped job shows as a gap rather than a flat line.
/// </summary>
internal sealed class HealthTelemetry
{
    /// <summary>Every <c>WaslaBid.*</c> source is registered by both hosts (<c>TelemetryModule</c>).</summary>
    public static readonly ActivitySource Source = new(TelemetryNames.Sources.Operations);

    public const string RunActivityName = "health check run";

    private readonly TimeProvider _clock;
    private readonly Histogram<double> _duration;
    private volatile LastRun? _lastRun;

    public HealthTelemetry(IMeterFactory meters, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _clock = clock;
        // The meter belongs to the factory, which disposes it with the host.
        var meter = meters.Create(TelemetryNames.Sources.Operations);
        meter.CreateObservableGauge(
            TelemetryNames.Metrics.HealthStatus, Measure, unit: null,
            "The last health result per component: 0 Healthy, 1 Degraded, 2 Unhealthy.");
        _duration = meter.CreateHistogram<double>(
            TelemetryNames.Metrics.HealthCheckDuration, "ms", "How long each health check took, per component.");
    }

    /// <summary>The span of one run; the checks' spans are its children.</summary>
    public static Activity? StartRun() => Source.StartActivity(RunActivityName);

    /// <summary>The span of one check, tagged with its component.</summary>
    public static Activity? StartCheck(string component) =>
        Source.StartActivity($"health check {component}")?.SetTag(TelemetryNames.Attributes.Component, component);

    /// <summary>Ends a check's span: status Error for Unhealthy, with no description (the message stays in the result).</summary>
    public static void EndCheck(Activity? activity, HealthStatus status)
    {
        if (activity is not null && status == HealthStatus.Unhealthy)
        {
            activity.SetStatus(ActivityStatusCode.Error);
        }
    }

    /// <summary>Records one check's duration.</summary>
    public void RecordDuration(string component, TimeSpan elapsed) =>
        _duration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>(TelemetryNames.MetricTags.Component, component));

    /// <summary>Replaces the gauge's results with this run's.</summary>
    public void Publish(IReadOnlyList<HealthResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        _lastRun = new LastRun([.. results], _clock.GetUtcNow());
    }

    private List<Measurement<int>> Measure()
    {
        var run = _lastRun;
        if (run is null || _clock.GetUtcNow() - run.At > HealthComponents.StaleAfter)
        {
            return [];
        }

        return
        [
            .. run.Results.Select(r => new Measurement<int>(
                StatusValue(r.Status), new KeyValuePair<string, object?>(TelemetryNames.MetricTags.Component, r.Component))),
        ];
    }

    /// <summary>Fixed values (spec 5.4), not the enum's order.</summary>
    private static int StatusValue(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => 0,
        HealthStatus.Degraded => 1,
        _ => 2,
    };

    private sealed record LastRun(IReadOnlyList<HealthResult> Results, DateTimeOffset At);
}
