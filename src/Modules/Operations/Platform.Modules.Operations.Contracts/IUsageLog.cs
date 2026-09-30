namespace Platform.Modules.Operations.Contracts;

/// <summary>
/// Distinct active users of a kind (<c>staff</c> or <c>vendor</c>) in a window (<c>1d</c>, <c>7d</c> or <c>30d</c>), in the
/// tenant with <paramref name="TenantSlug"/>, or across tenants, each user once, when it is null (W-10, spec 6.4). The
/// values are those of <c>Platform.Shared.Telemetry.TelemetryNames</c>.
/// </summary>
public sealed record ActiveUserCount(string? TenantSlug, string Kind, string Window, int Users);

/// <summary>One result of the usage job: every count, and when it was computed.</summary>
public sealed record UsageCounts(DateTimeOffset ComputedAt, IReadOnlyList<ActiveUserCount> Counts)
{
    /// <summary>A result older than this is not shown or reported: a stopped job shows as a gap (spec 6.4, 6.6).</summary>
    public static TimeSpan StaleAfter { get; } = TimeSpan.FromMinutes(15);

    public bool IsStaleAt(DateTimeOffset now) => now - ComputedAt > StaleAfter;
}

/// <summary>
/// The usage job's latest stored result (<c>ops.active_user_counts</c>, spec 6.6), for the platform console's usage page;
/// the same pattern as <see cref="IHealthLog"/>. Readable only in a scope with neither a tenant nor a vendor context.
/// </summary>
public interface IUsageLog
{
    /// <summary>The latest result, or null before the job's first run.</summary>
    Task<UsageCounts?> LatestAsync(CancellationToken cancellationToken = default);
}
