using System.Diagnostics;
using Microsoft.AspNetCore.StaticAssets;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Platform.Shared.Telemetry;

namespace Platform.Web.Telemetry;

/// <summary>
/// The web host's part of the telemetry registration (W-10, plan task 1; spec 5.2, 5.4, O-15), on top of
/// <c>AddPlatformTelemetry</c>: a server span per HTTP request, the ASP.NET Core and Kestrel meters, and the Blazor
/// activity sources and meters .NET 10 publishes. Kept out of <c>Platform.Shared</c> so the worker gains no ASP.NET Core
/// framework reference.
/// </summary>
internal static class WebTelemetry
{
    /// <summary>
    /// The Blazor activity sources of .NET 10 (<c>ComponentsActivitySource</c>: route, navigation and event-handler spans;
    /// <c>CircuitActivitySource</c>: circuit start spans).
    /// </summary>
    private static readonly string[] ComponentSources =
    [
        "Microsoft.AspNetCore.Components",
        "Microsoft.AspNetCore.Components.Server.Circuits",
    ];

    /// <summary>
    /// Request duration and active requests, Kestrel connections, and the Blazor meters of .NET 10 (<c>ComponentsMetrics</c>,
    /// its lifecycle meter, <c>CircuitMetrics</c>).
    /// </summary>
    private static readonly string[] Meters =
    [
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Components",
        "Microsoft.AspNetCore.Components.Lifecycle",
        "Microsoft.AspNetCore.Components.Server.Circuits",
    ];

    /// <summary>
    /// Paths that never get a span (spec 5.2): readiness and liveness probes, the on-demand TLS ask, the framework's files,
    /// the class libraries' static assets, and the Blazor hub (its negotiation, and the WebSocket request that lives as long as the circuit; the
    /// circuit's own work is traced by the Blazor activity sources). Matched by path because the instrumentation's
    /// filter runs when the request starts, before routing has chosen an endpoint; the children of an unsampled request (a
    /// probe's database call) are not sampled either. Static assets mapped elsewhere (the scoped CSS bundle
    /// <c>/Platform.Web.styles.css</c>) are dropped when the request ends, by their endpoint (<see cref="DropStaticAsset"/>).
    /// </summary>
    private static readonly PathString[] Untraced =
    [
        "/health",
        "/alive",
        // Caddy's on-demand TLS ask (Edge/TlsAsk): one call per new host name, refusals included; allowed names are logged.
        "/internal/tls-ask",
        "/_framework",
        "/_content",
        "/_blazor",
    ];

    public static IHostApplicationBuilder AddWebTelemetry(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // O-8: hosting takes the inbound trace context from this propagator, which extracts nothing (task 2).
        builder.Services.AddSingleton<DistributedContextPropagator, UntrustedTraceContextPropagator>();
        // O-10: a handled exception's type and Error status on the server span (the instrumentation covers the unhandled ones).
        builder.Services.AddExceptionHandler<ExceptionSpanHandler>();
        // O-8, second half: the ASP.NET Core instrumentation extracts the headers a second time with OpenTelemetry's default
        // propagator, and re-parents the request onto what it finds, unless that propagator is the plain W3C one, whose
        // extraction it leaves to hosting (above). The SDK's default adds W3C baggage, which would bring that second
        // extraction back. Process-wide by OpenTelemetry's design; outbound HttpClient calls still carry traceparent, which
        // the runtime injects.
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = context => IsTraced(context.Request.Path);
                    // The only response enrichment slot: a later enricher must call DropStaticAsset from its own callback.
                    options.EnrichWithHttpResponse = DropStaticAsset;
                    // No exception event, which could not be masked; the type and Error status instead (O-10). The masked
                    // message is on the exception handler's log record of the same trace.
                    options.RecordException = false;
                    options.EnrichWithException = SpanExceptions.Record;
                })
                .AddSource(ComponentSources))
            .WithMetrics(metrics => metrics.AddMeter(Meters));
        return builder;
    }

    /// <summary>False for a path under one of the untraced prefixes, segment by segment and ignoring case.</summary>
    public static bool IsTraced(PathString path) =>
        !Untraced.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Drops the span of a request that a static asset endpoint (<c>MapStaticAssets</c>) served, wherever its path. Runs as the
    /// request ends, before the span is handed to the exporters, which skip a span that is no longer recorded.
    /// </summary>
    internal static void DropStaticAsset(Activity activity, HttpResponse response)
    {
        if (response.HttpContext.GetEndpoint()?.Metadata.GetMetadata<StaticAssetDescriptor>() is not null)
        {
            activity.IsAllDataRequested = false;
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
