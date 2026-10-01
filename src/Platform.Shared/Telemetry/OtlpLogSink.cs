using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;

namespace Platform.Shared.Telemetry;

/// <summary>
/// Sends the host's log events to the OpenTelemetry Collector over OTLP gRPC (W-10, spec Q2: Serilog kept), batched and
/// asynchronous. Registered only when an endpoint is configured (<see cref="TelemetryModule.OtlpEndpoint"/>); an unreachable
/// collector loses the batch and never fails the caller (Serilog reports export failures to its SelfLog only).
/// </summary>
/// <remarks>
/// The record carries the rendered message as its body, the template in <c>message_template.text</c>, every property
/// (log scopes included) as an attribute, <c>SourceContext</c> (the logger category) as the instrumentation scope and as an
/// attribute, and the trace and span ids Serilog took from <see cref="System.Diagnostics.Activity.Current"/> when the record
/// was written. The OTLP environment variables are not read by the sink itself: the endpoint comes from configuration, the
/// resource from <see cref="TelemetryResource"/>, so logs, spans and metrics name the same service.
/// </remarks>
internal sealed class OtlpLogSink : ILogEventSink, IDisposable
{
    private readonly Logger _exporter;
    private int _disposed;

    public OtlpLogSink(TelemetryResource resource, IConfiguration configuration)
    {
        var endpoint = TelemetryModule.OtlpEndpoint(configuration)
            ?? throw new InvalidOperationException($"Setting '{TelemetryModule.OtlpEndpointSetting}' is not configured; the OTLP log sink needs it.");
        _exporter = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.OpenTelemetry(
                options =>
                {
                    // Logs only: with Endpoint set, the sink would also turn span-shaped events into traces.
                    options.Endpoint = null;
                    options.LogsEndpoint = endpoint.ToString();
                    options.Protocol = OtlpProtocol.Grpc;
                    options.ResourceAttributes = new Dictionary<string, object>(resource.Attributes);
                    options.IncludedData = IncludedData.MessageTemplateTextAttribute
                        | IncludedData.TraceIdField
                        | IncludedData.SpanIdField
                        | IncludedData.SourceContextAttribute
                        | IncludedData.SpecRequiredResourceAttributes;
                    // The export's own gRPC call must not become an HttpClient span of the host.
                    options.OnBeginSuppressInstrumentation = SuppressInstrumentationScope.Begin;
                },
                ignoreEnvironment: true)
            .CreateLogger();
    }

    public void Emit(LogEvent logEvent) => _exporter.Write(logEvent);

    /// <summary>Flushes the last batch. Called by the host's Serilog logger and again by the container; the second call does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _exporter.Dispose();
        }
    }
}
