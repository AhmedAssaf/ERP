using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog.Core;
using Serilog.Events;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// One log event as the host's Serilog pipeline handed it to its sinks (W-10, after every enricher): the logger category
/// (<c>SourceContext</c>), the rendered message, the template, each property as text, and the trace and span ids Serilog took
/// from <see cref="Activity.Current"/>. <see cref="Event"/> is the event itself, for anything the projection leaves out.
/// </summary>
internal sealed record CapturedLog(
    string? Category,
    LogEventLevel Level,
    string Message,
    string Template,
    IReadOnlyDictionary<string, string?> Properties,
    ActivityTraceId? TraceId,
    ActivitySpanId? SpanId,
    Exception? Exception,
    LogEvent Event);

/// <summary>
/// In-memory exporters for one host's telemetry (W-10, plan task 1), added through <c>ConfigureTestServices</c> or a
/// <see cref="JobServerHost"/>'s service callback with <see cref="AddTo"/>: spans and metrics through the OpenTelemetry SDK's
/// in-memory exporters, log events through a Serilog sink beside the OTLP one (logs go Serilog to OTLP, spec Q2).
/// </summary>
/// <remarks>
/// Activity listeners are process-wide: a host's tracer provider also exports the spans of every other host running in the
/// test process (parallel test classes). <see cref="SpansOf"/> and <see cref="ServerSpansOf"/> keep only the requests one web
/// host served. Logs are per host: the sink sits in that host's own Serilog logger.
/// </remarks>
internal sealed class CapturedTelemetry
{
    private readonly LockedList<Activity> _spans = new();
    private readonly LockedList<MetricSnapshot> _metrics = new();
    private readonly LogSink _logs = new();

    /// <summary>Adds the in-memory span and metric exporters and the log sink to a host's services.</summary>
    public void AddTo(IServiceCollection services)
    {
        services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(_spans));
        services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddInMemoryExporter(_metrics));
        services.AddSingleton<ILogEventSink>(_logs);
    }

    /// <summary>Every span the host's tracer provider exported, other hosts' included (see remarks).</summary>
    public IReadOnlyList<Activity> AllSpans => _spans.Snapshot();

    /// <summary>
    /// The spans of the requests one web host served: its ASP.NET Core server spans (from the host's own
    /// <see cref="ActivitySource"/> in its container) and every in-process span below them, such as Npgsql commands.
    /// </summary>
    public IReadOnlyList<Activity> SpansOf(IServiceProvider webHost)
    {
        var source = webHost.GetRequiredService<ActivitySource>();
        return [.. AllSpans.Where(span => ReferenceEquals(LocalRoot(span).Source, source))];
    }

    /// <summary>The ASP.NET Core server spans of the requests one web host served.</summary>
    public IReadOnlyList<Activity> ServerSpansOf(IServiceProvider webHost) =>
        [.. SpansOf(webHost).Where(span => span.Kind == ActivityKind.Server)];

    /// <summary>
    /// Waits until the web host has exported at least <paramref name="count"/> server spans and returns all of them. A
    /// response can reach the test client a moment before the server span ends and is exported.
    /// </summary>
    public async Task<IReadOnlyList<Activity>> WaitForServerSpansAsync(IServiceProvider webHost, int count, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var spans = ServerSpansOf(webHost);
            if (spans.Count >= count || DateTime.UtcNow > deadline)
            {
                return spans;
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    /// <summary>Every log event the host wrote since it started or since <see cref="Clear"/>.</summary>
    public IReadOnlyList<CapturedLog> Logs => _logs.Snapshot();

    /// <summary>Collects the host's metrics now and returns that collection's points (cumulative since the host started).</summary>
    public IReadOnlyList<MetricSnapshot> CollectMetrics(IServiceProvider host)
    {
        _metrics.Clear();
        host.GetRequiredService<MeterProvider>().ForceFlush(10_000).ShouldBeTrue("the meter provider collected within ten seconds");
        return _metrics.Snapshot();
    }

    /// <summary>Forgets what was captured so far, for example the spans and logs of host startup.</summary>
    public void Clear()
    {
        _spans.Clear();
        _metrics.Clear();
        _logs.Clear();
    }

    private static Activity LocalRoot(Activity span)
    {
        var current = span;
        while (current.Parent is { } parent)
        {
            current = parent;
        }

        return current;
    }

    private sealed class LogSink : ILogEventSink
    {
        private readonly LockedList<CapturedLog> _events = new();

        public void Emit(LogEvent logEvent)
        {
            var properties = logEvent.Properties.ToDictionary(
                p => p.Key,
                p => p.Value is ScalarValue scalar ? Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) : p.Value.ToString(),
                StringComparer.Ordinal);
            _events.Add(new CapturedLog(
                properties.GetValueOrDefault(Constants.SourceContextPropertyName),
                logEvent.Level,
                logEvent.RenderMessage(CultureInfo.InvariantCulture),
                logEvent.MessageTemplate.Text,
                properties,
                logEvent.TraceId,
                logEvent.SpanId,
                logEvent.Exception,
                logEvent));
        }

        public IReadOnlyList<CapturedLog> Snapshot() => _events.Snapshot();

        public void Clear() => _events.Clear();
    }

    /// <summary>The in-memory exporters add from export threads while a test reads; every access takes the lock.</summary>
    private sealed class LockedList<T> : ICollection<T>
    {
        private readonly Lock _lock = new();
        private readonly List<T> _items = [];

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _items.Count;
                }
            }
        }

        public bool IsReadOnly => false;

        public void Add(T item)
        {
            lock (_lock)
            {
                _items.Add(item);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _items.Clear();
            }
        }

        public bool Contains(T item)
        {
            lock (_lock)
            {
                return _items.Contains(item);
            }
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            lock (_lock)
            {
                _items.CopyTo(array, arrayIndex);
            }
        }

        public bool Remove(T item)
        {
            lock (_lock)
            {
                return _items.Remove(item);
            }
        }

        public IReadOnlyList<T> Snapshot()
        {
            lock (_lock)
            {
                return [.. _items];
            }
        }

        public IEnumerator<T> GetEnumerator() => Snapshot().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
