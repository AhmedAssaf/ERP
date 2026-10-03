using System.Diagnostics.Metrics;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Operations.Health;
using Platform.Shared.Telemetry;

namespace Platform.UnitTests.Operations;

/// <summary>
/// W-10 follow-up (final review, task 4; closed 2026-10-03): the health status gauge reports the last run only while it is
/// fresh, up to <see cref="HealthComponents.StaleAfter"/> (120 seconds, the board's own cut-off, spec 3.2), so a stopped job
/// shows as a gap in the metrics rather than a flat line. Read with a <see cref="MeterListener"/> bound to this test's own
/// meter factory, so no other test's gauge is observed.
/// </summary>
public sealed class HealthTelemetryGaugeTests
{
    [Fact]
    public void The_status_gauge_reports_the_last_run_up_to_120_seconds_and_nothing_after()
    {
        HealthComponents.StaleAfter.ShouldBe(TimeSpan.FromSeconds(120));
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        using var meters = new TestMeterFactory();
        var telemetry = new HealthTelemetry(meters, clock);

        Observe(meters).ShouldBeEmpty("nothing before the first run");

        telemetry.Publish(
        [
            new HealthResult(HealthComponents.PostgreSql, HealthStatus.Healthy, 3, clock.GetUtcNow()),
            new HealthResult(HealthComponents.Keycloak, HealthStatus.Degraded, 9, clock.GetUtcNow()),
        ]);
        Observe(meters).ShouldBe([(HealthComponents.PostgreSql, 0), (HealthComponents.Keycloak, 1)], ignoreOrder: true);

        clock.Advance(HealthComponents.StaleAfter);
        Observe(meters).Count.ShouldBe(2, "still fresh at exactly 120 seconds");

        clock.Advance(TimeSpan.FromSeconds(1));
        Observe(meters).ShouldBeEmpty("stale after 120 seconds: a gap, not a flat line");

        telemetry.Publish([new HealthResult(HealthComponents.PostgreSql, HealthStatus.Unhealthy, 5000, clock.GetUtcNow())]);
        Observe(meters).ShouldBe([(HealthComponents.PostgreSql, 2)]);
    }

    private static List<(string Component, int Value)> Observe(TestMeterFactory meters)
    {
        var points = new List<(string, int)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, meters) && instrument.Name == TelemetryNames.Metrics.HealthStatus)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == TelemetryNames.MetricTags.Component)
                {
                    points.Add(((string)tag.Value!, value));
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return points;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
            {
                meter.Dispose();
            }
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
