using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;

namespace Platform.Shared.Telemetry;

/// <summary>
/// The telemetry registration both hosts share (W-10, plan task 1; spec O-3, O-5, O-6, O-16; Q2 decided 2026-10-01: Serilog
/// kept for logs). Traces and metrics go through the OpenTelemetry SDK, logs through Serilog registered as one
/// Microsoft.Extensions.Logging provider; all three leave over OTLP gRPC to the collector, and only when an endpoint is set.
/// The web host adds its ASP.NET Core parts on top (<c>Platform.Web.Telemetry.WebTelemetry</c>); this project stays free of
/// the ASP.NET Core framework, since the worker is built from it.
/// </summary>
public static class TelemetryModule
{
    /// <summary>
    /// The collector's OTLP gRPC endpoint, for example <c>http://localhost:4317</c>. Absent: <see cref="StandardOtlpEndpointVariable"/>
    /// is read instead; present but empty: off, with no fallback. Required outside Development and Testing.
    /// </summary>
    public const string OtlpEndpointSetting = "Telemetry:OtlpEndpoint";

    /// <summary>The standard OpenTelemetry variable, read only when <see cref="OtlpEndpointSetting"/> is absent.</summary>
    public const string StandardOtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary><c>deployment.environment.name</c> (<c>development</c>, <c>pilot</c>); unset: the host environment in lower case.</summary>
    public const string EnvironmentSetting = "Telemetry:Environment";

    /// <summary>Built-in meters of .NET 9 and later and of Npgsql (spec 5.4): runtime, connection pool, HttpClient.</summary>
    private static readonly string[] Meters = ["System.Runtime", TelemetryNames.Sources.Npgsql, "System.Net.Http", TelemetryNames.Sources.OwnPrefix];

    /// <summary>
    /// Registers the resource, tracing (Npgsql, every <c>WaslaBid.*</c> source, HttpClient; parent-based, everything
    /// sampled, O-5), metrics (runtime, Npgsql, HttpClient, every <c>WaslaBid.*</c> meter) and logging (Serilog as a provider),
    /// with the OTLP exporters only when <see cref="OtlpEndpoint"/> gives an endpoint. Outside Development and Testing every
    /// other logging provider is removed first, the console included (O-16): logs then leave only through OTLP, so there an
    /// endpoint is required and its absence stops the host.
    /// </summary>
    public static IHostApplicationBuilder AddPlatformTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var resource = TelemetryResource.For(serviceName, builder.Environment, builder.Configuration);
        var endpoint = OtlpEndpoint(builder.Configuration);
        if (endpoint is null && !IsDevelopmentOrTesting(builder.Environment))
        {
            // Without the console (O-16) and without a collector, every log line would be dropped without a trace.
            throw new InvalidOperationException(
                $"Setting '{OtlpEndpointSetting}' is not configured. It is required outside Development: logs leave the host only over OTLP.");
        }

        builder.Services.AddSingleton(resource);
        AddLogging(builder, endpoint is not null);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(resource.ServiceName, serviceVersion: resource.ServiceVersion, autoGenerateServiceInstanceId: false, serviceInstanceId: resource.ServiceInstanceId)
                .AddAttributes([new(TelemetryNames.Resource.DeploymentEnvironment, resource.DeploymentEnvironment)]))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))
                    .AddSource(TelemetryNames.Sources.Npgsql, TelemetryNames.Sources.OwnPrefix)
                    .AddHttpClientInstrumentation(options =>
                    {
                        // No exception event, which could not be masked; the type and Error status instead (O-10).
                        options.RecordException = false;
                        options.EnrichWithException = SpanExceptions.Record;
                    })
                    .AddProcessor(new RootDatabaseSpanFilter());
                // The last processor, ahead of every exporter (O-10): no span leaves before its values are masked.
                tracing.AddProcessor(new RedactingSpanProcessor());
                if (endpoint is not null)
                {
                    tracing.AddOtlpExporter(options => UseCollector(options, endpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(Meters);
                if (endpoint is not null)
                {
                    metrics.AddOtlpExporter(options => UseCollector(options, endpoint));
                }
            });

        return builder;
    }

    /// <summary>
    /// The collector endpoint from <see cref="OtlpEndpointSetting"/> when that setting is present, an empty value meaning off;
    /// only when it is absent, <see cref="StandardOtlpEndpointVariable"/> (the hosts read environment variables into
    /// configuration). Null when there is none. A value that is not an absolute http or https URL stops the host, naming the
    /// setting and never the value.
    /// </summary>
    public static Uri? OtlpEndpoint(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var (setting, value) = configuration[OtlpEndpointSetting] is { } own
            ? (OtlpEndpointSetting, own)
            : (StandardOtlpEndpointVariable, configuration[StandardOtlpEndpointVariable]);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https"
            ? endpoint
            : throw new InvalidOperationException($"Setting '{setting}' is not an absolute http or https URL of the OTLP collector.");
    }

    private static bool IsDevelopmentOrTesting(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    /// <summary>
    /// Serilog only as a provider: never <c>UseSerilog()</c> or <c>services.AddSerilog()</c>, which replace the logger factory
    /// and bypass the <c>LoggerFilterOptions</c> rules (W-24's N-10 cap on Data Protection and its startup guard). Serilog's
    /// own minimum level is Verbose, so those rules and <c>Logging:LogLevel</c> stay the only level gate. Registered directly
    /// rather than through <c>builder.Logging.AddSerilog(logger, dispose: true)</c>, for two reasons: that extension also adds
    /// a Trace rule for the Serilog provider, which outranks every provider-neutral <c>Logging:LogLevel</c> rule and would send
    /// Debug and Trace records of every category to the collector; and the logger is built from the container, so the OTLP
    /// sink and any other <see cref="ILogEventSink"/> registered as a service (a test's in-memory sink) receive every event.
    /// </summary>
    private static void AddLogging(IHostApplicationBuilder builder, bool exportToCollector)
    {
        if (!IsDevelopmentOrTesting(builder.Environment))
        {
            // O-16: no second, unredacted, unrotated copy on container stdout. Startup failures before the host is built
            // still reach stderr as unhandled exceptions.
            builder.Logging.ClearProviders();
        }

        // The default host adds TraceId, SpanId and ParentId to every record as a scope (activity tracking). The OTLP record
        // carries trace_id and span_id itself, Serilog takes them from Activity.Current; the scope copies would only be masked
        // as long numbers when all their hexadecimal digits happen to be decimal ones (W-10 final fix wave).
        builder.Logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.None);

        if (exportToCollector)
        {
            builder.Services.AddSingleton<ILogEventSink, OtlpLogSink>();
        }

        builder.Services.AddSingleton<ILoggerProvider>(services => new SerilogLoggerProvider(
            CreateLogger(services.GetRequiredService<TelemetryResource>().ServiceName, services.GetServices<ILogEventSink>()), dispose: true));
    }

    /// <summary>
    /// The host's Serilog logger: no level of its own, log scopes and <c>LogContext</c> properties as event properties, the O-9
    /// context of the current span for a record written outside those scopes (<see cref="ActivityContextEnricher"/>), the
    /// component (<see cref="ComponentEnricher"/>), and every <see cref="ILogEventSink"/> service as a sink. No console sink,
    /// in any environment (O-16). Redaction (O-10, plan task 3): request types and streams are never destructured
    /// (<see cref="RedactingDestructuringPolicy"/>, within depth and count caps); <see cref="RedactingEnricher"/>, the last
    /// enricher, masks every string value and then cuts it to 4096 characters (masked first, so no address is cut in half),
    /// and adds the exception's type and masked message and stack; each sink is wrapped in <see cref="RedactedEventSink"/>,
    /// so it never receives the raw exception or unmasked template text.
    /// </summary>
    internal static Logger CreateLogger(string serviceName, IEnumerable<ILogEventSink> sinks)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Destructure.With<RedactingDestructuringPolicy>()
            .Destructure.ToMaximumDepth(4)
            .Destructure.ToMaximumCollectionCount(32)
            .Enrich.FromLogContext()
            .Enrich.With<ActivityContextEnricher>()
            .Enrich.With(new ComponentEnricher(serviceName))
            .Enrich.With<RedactingEnricher>();
        foreach (var sink in sinks)
        {
            configuration.WriteTo.Sink(new RedactedEventSink(sink));
        }

        return configuration.CreateLogger();
    }

    private static void UseCollector(OtlpExporterOptions options, Uri endpoint)
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.Grpc;
    }
}
