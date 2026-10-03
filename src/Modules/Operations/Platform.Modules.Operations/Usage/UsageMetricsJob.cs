using Hangfire;
using Microsoft.Extensions.Logging;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Telemetry;
using Platform.Shared.Jobs;

namespace Platform.Modules.Operations.Usage;

/// <summary>
/// The recurring platform job "usage-metrics" (W-10, spec 6.4), every five minutes in the worker, with no tenant: counts
/// the active users through <see cref="IUserActivityCounts"/> (Identity), labels them with tenant slugs
/// (<see cref="ITenantSlugs"/>, Tenancy), stores the result in <c>ops.active_user_counts</c> for the console and keeps it
/// in <see cref="UsageSnapshot"/> for the gauges, so the console and the metrics show the same numbers. Every tenant, kind
/// and window has a count, zero when nobody was active, so the page and the series show zero rather than nothing.
/// "usage-activity-prune" runs <see cref="PruneAsync"/> once a day: hourly buckets older than 35 days are deleted
/// (spec 6.4 and 6.8, Q7 as recommended). Either job failing fails its run, so F-60's job-failure alert applies.
/// </summary>
[PlatformJob]
internal sealed partial class UsageMetricsJob(
    IUserActivityCounts activity,
    ITenantSlugs tenantSlugs,
    UsageLog log,
    UsageSnapshot snapshot,
    TimeProvider clock,
    ILogger<UsageMetricsJob> logger)
{
    /// <summary>How long hourly activity buckets are kept.</summary>
    public static TimeSpan Retention { get; } = TimeSpan.FromDays(35);

    private static readonly (ActivityKind Kind, string Value)[] Kinds =
        [(ActivityKind.Staff, TelemetryNames.UserKinds.Staff), (ActivityKind.Vendor, TelemetryNames.UserKinds.Vendor)];

    private static readonly (ActivityWindow Window, string Value)[] Windows =
    [
        (ActivityWindow.OneDay, TelemetryNames.Windows.OneDay),
        (ActivityWindow.SevenDays, TelemetryNames.Windows.SevenDays),
        (ActivityWindow.ThirtyDays, TelemetryNames.Windows.ThirtyDays),
    ];

    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var counts = (await activity.CountAsync(now, cancellationToken))
            .ToDictionary(c => (c.TenantId, c.Kind, c.Window), c => c.Users);
        var slugs = await tenantSlugs.ListAsync(cancellationToken);

        // Per tenant (by slug; a tenant no longer listed drops out) and across tenants (null), every kind and window.
        var scopes = slugs.Select(s => (TenantId: (Guid?)s.Key, Slug: (string?)s.Value)).Append((TenantId: null, Slug: null));
        var rows = (from scope in scopes
                    from kind in Kinds
                    from window in Windows
                    select new ActiveUserCount(
                        scope.Slug, kind.Value, window.Value, counts.GetValueOrDefault((scope.TenantId, kind.Kind, window.Window)))).ToList();

        var result = new UsageCounts(now, rows);
        await log.ReplaceAsync(result, cancellationToken);
        snapshot.Set(result);
    }

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        var pruned = await activity.PruneAsync(clock.GetUtcNow() - Retention, cancellationToken);
        LogPruned(logger, pruned);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Count} hourly activity buckets older than the retention.")]
    private static partial void LogPruned(ILogger logger, int count);
}
