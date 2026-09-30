using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Telemetry;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>One measurement of an instrument of the <c>WaslaBid.Usage</c> meter, with its tags.</summary>
internal sealed record MetricPoint(string Name, long Value, IReadOnlyDictionary<string, string?> Tags)
{
    public bool HasExactly(params (string Key, string Value)[] tags) =>
        Tags.Count == tags.Length && tags.All(t => Tags.TryGetValue(t.Key, out var value) && value == t.Value);
}

/// <summary>
/// Reads the observable instruments of the <c>WaslaBid.Usage</c> meter created by one <see cref="IMeterFactory"/>, as an
/// exporter's collection would (spec 6.1). A plain <see cref="MeterListener"/>: the OpenTelemetry SDK and its in-memory
/// exporter come with the telemetry pipeline (plan task 1), which is not built yet. Scoped to the one factory, so the
/// instruments of other hosts running in the same process are never read.
/// </summary>
internal sealed class UsageMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Lock _lock = new();
    private readonly List<MetricPoint> _points = [];

    public UsageMetrics(IMeterFactory factory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == TelemetryNames.UsageMeter && ReferenceEquals(instrument.Meter.Scope, factory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>A metrics container of its own, as each host has one.</summary>
    public static ServiceProvider NewMeterFactoryHost() => new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>Every point the observable instruments report now.</summary>
    public IReadOnlyList<MetricPoint> Collect()
    {
        lock (_lock)
        {
            _points.Clear();
        }

        _listener.RecordObservableInstruments();
        lock (_lock)
        {
            return [.. _points];
        }
    }

    /// <summary>The value reported for exactly these tags, or null when no point carries exactly them.</summary>
    public long? Value(string name, params (string Key, string Value)[] tags) =>
        Collect().SingleOrDefault(p => p.Name == name && p.HasExactly(tags))?.Value;

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value?.ToString();
        }

        lock (_lock)
        {
            _points.Add(new MetricPoint(instrument.Name, value, copy));
        }
    }
}
