using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Platform.Shared.Telemetry;

/// <summary>
/// O-9 for the records written outside the request's or job's log scopes: an unhandled exception is logged by the exception
/// handler (or Development's exception page) after the scopes of the middleware below it have closed, but still inside the
/// server span, whose tags hold the same context. For each context key an event lacks, this enricher takes the value from
/// the nearest span that has it, walking from <see cref="Activity.Current"/> up through its in-process parents to the local
/// root; a remote parent (another process's span) ends the walk, so nothing is read from outside this process. A value
/// already on the event (from a scope) always wins.
/// </summary>
/// <remarks>
/// The keys are the pseudonymous ids of O-9 only, the same ones the spans are tagged with: never a free-form tag.
/// </remarks>
public sealed class ActivityContextEnricher : ILogEventEnricher
{
    private static readonly string[] ContextKeys =
    [
        TelemetryNames.Attributes.TenantId,
        TelemetryNames.Attributes.TenantSlug,
        TelemetryNames.Attributes.UserId,
        TelemetryNames.Attributes.VendorCompanyId,
        TelemetryNames.Attributes.JobId,
        TelemetryNames.Attributes.JobType,
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);
        if (Activity.Current is null || ContextKeys.All(logEvent.Properties.ContainsKey))
        {
            return;
        }

        for (var span = Activity.Current; span is not null; span = span.Parent)
        {
            foreach (var key in ContextKeys)
            {
                if (!logEvent.Properties.ContainsKey(key) && span.GetTagItem(key) is { } value)
                {
                    logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(key, value));
                }
            }
        }
    }
}
