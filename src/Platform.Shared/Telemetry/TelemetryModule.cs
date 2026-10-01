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
    /// <summary>The collector's OTLP gRPC endpoint, for example <c>http://localhost:4317</c>. Unset: nothing is exported.</summary>
    public const string OtlpEndpointSetting = "Telemetry:OtlpEndpoint";

    /// <summary>The standard OpenTelemetry variable, read when <see cref="OtlpEndpointSetting"/> is not set.</summary>
    public const string StandardOtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary><c>deployment.environment.name</c> (<c>development</c>, <c>pilot</c>); unset: the host environment in lower case.</summary>
    public const string EnvironmentSetting = "Telemetry:Environment";

    /// <summary>Built-in meters of .NET 9 and later and of Npgsql (spec 5.4): runtime, connection pool, HttpClient.</summary>
    private static readonly string[] Meters = ["System.Runtime", TelemetryNames.Sources.Npgsql, "System.Net.Http", TelemetryNames.Sources.OwnPrefix];

    /// <summary>
    /// Registers the resource, tracing (Npgsql, every <c>WaslaBid.*</c> source, HttpClient; parent-based, everything
    /// sampled, O-5), metrics (runtime, Npgsql, HttpClient, every <c>WaslaBid.*</c> meter) and logging (Serilog as a provider),
    /// with the OTLP exporters only when <see cref="OtlpEndpoint"/> gives an endpoint. Outside Development and Testing every
    /// other logging provider is removed first, the console included (O-16): logs then leave only through OTLP.
    /// </summary>
    public static IHostApplicationBuilder AddPlatformTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var resource = TelemetryResource.For(serviceName, builder.Environment, builder.Configuration);
        var endpoint = OtlpEndpoint(builder.Configuration);
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
                    .AddHttpClientInstrumentation();
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
    /// The collector endpoint from <see cref="OtlpEndpointSetting"/>, or else <see cref="StandardOtlpEndpointVariable"/> (the
    /// hosts read environment variables into configuration); null when neither is set. A value that is not an absolute URL
    /// stops the host, naming the setting.
    /// </summary>
    public static Uri? OtlpEndpoint(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var (setting, value) = configuration[OtlpEndpointSetting] is { Length: > 0 } own
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

        if (exportToCollector)
        {
            builder.Services.AddSingleton<ILogEventSink, OtlpLogSink>();
        }

        builder.Services.AddSingleton<ILoggerProvider>(services => new SerilogLoggerProvider(CreateLogger(services), dispose: true));
    }

    /// <summary>
    /// The host's Serilog logger: no level of its own, log scopes and <c>LogContext</c> properties as event properties, and
    /// every <see cref="ILogEventSink"/> service as a sink. No console sink, in any environment (O-16).
    /// </summary>
    private static Logger CreateLogger(IServiceProvider services)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext();
        foreach (var sink in services.GetServices<ILogEventSink>())
        {
            configuration.WriteTo.Sink(sink);
        }

        return configuration.CreateLogger();
    }

    private static void UseCollector(OtlpExporterOptions options, Uri endpoint)
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.Grpc;
    }
}
