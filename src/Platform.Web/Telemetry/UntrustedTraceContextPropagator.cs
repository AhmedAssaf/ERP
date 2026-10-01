using System.Diagnostics;

namespace Platform.Web.Telemetry;

/// <summary>
/// O-8: a <c>traceparent</c>, <c>tracestate</c> or <c>baggage</c> sent from the internet is not trusted. ASP.NET Core hosting
/// asks the <see cref="DistributedContextPropagator"/> in the container for the inbound context; this one extracts nothing, so
/// every request starts its own trace: a client can neither choose a trace id (and collide with another request in F-53's
/// lookup) nor switch sampling off with an unsampled flag (the parent-based sampler never sees a remote parent). Outbound
/// calls inject as the runtime's default propagator does.
/// </summary>
internal sealed class UntrustedTraceContextPropagator : DistributedContextPropagator
{
    private readonly DistributedContextPropagator _outbound = CreateDefaultPropagator();

    public override IReadOnlyCollection<string> Fields => _outbound.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        _outbound.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(
        object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
    {
        traceId = null;
        traceState = null;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}
