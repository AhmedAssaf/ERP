using System.Diagnostics;
using OpenTelemetry;

namespace Platform.Shared.Telemetry;

/// <summary>
/// Records an Npgsql command span only below another span, a request or a job (W-10 fix round 1). Database work outside any
/// activity, such as Hangfire's queue polling, heartbeats and locks or the Data Protection key-ring refresh, would otherwise
/// start a trace of its own every few seconds. Runs when the span starts: a span no longer recorded is never handed to an
/// exporter, whatever the order of the processors.
/// </summary>
/// <remarks>
/// A processor rather than a sampler: the sampler's parameters carry the span's name and parent but not its source, and
/// Npgsql names its spans after the operation (or the database), not after itself.
/// </remarks>
internal sealed class RootDatabaseSpanFilter : BaseProcessor<Activity>
{
    public override void OnStart(Activity data)
    {
        if (data.ParentSpanId == default && data.Source.Name == TelemetryNames.Sources.Npgsql)
        {
            data.IsAllDataRequested = false;
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
