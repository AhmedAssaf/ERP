namespace Platform.Modules.Identity.Contracts;

/// <summary>The kinds of user whose activity is recorded (spec 6.2, 6.4); platform admins are counted only while connected.</summary>
public enum ActivityKind
{
    Staff,
    Vendor,
}

/// <summary>The rolling windows of the active-user counts, to the hour (spec 6.4).</summary>
public enum ActivityWindow
{
    OneDay,
    SevenDays,
    ThirtyDays,
}

/// <summary>Distinct users of a kind active in a window: in one tenant, or across all tenants (each user once) when <paramref name="TenantId"/> is null.</summary>
public sealed record ActivityCount(Guid? TenantId, ActivityKind Kind, ActivityWindow Window, int Users);

/// <summary>
/// Records that the current request's or circuit's user was active, as its host set the scope: its tenant, acting user
/// (the <c>sub</c>) and, for a vendor, its vendor context (W-10, spec 6.4). At most one write per tenant, user, kind and
/// hour; a failed write is logged by type and never fails the caller. Without a tenant or an acting user nothing is written.
/// </summary>
public interface IUserActivityRecorder
{
    Task RecordAsync(ActivityKind kind, CancellationToken cancellationToken = default);
}

/// <summary>
/// Counts and prunes the recorded activity (spec 6.4), for the worker's usage job only: a scope with a tenant or a vendor
/// context is refused by the database. Returns counts, never who.
/// </summary>
public interface IUserActivityCounts
{
    /// <summary>Distinct users per tenant, kind and window, and across tenants, for the windows ending at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<ActivityCount>> CountAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Deletes the hourly buckets older than <paramref name="before"/>; returns how many.</summary>
    Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
}
