using Microsoft.EntityFrameworkCore;
using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations.Usage;

/// <summary>A row of <c>ops.active_user_counts</c> (operations migration 0006).</summary>
internal sealed class ActiveUserCountRow
{
    public string? TenantSlug { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string TimeWindow { get; set; } = string.Empty;

    public int Users { get; set; }

    public DateTimeOffset ComputedAt { get; set; }
}

/// <summary>Stores and reads the usage job's latest result (spec 6.6); the job replaces every row in one transaction.</summary>
internal sealed class UsageLog(IDbContextFactory<OperationsDbContext> contexts) : IUsageLog
{
    public async Task<UsageCounts?> LatestAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.ActiveUserCounts.AsNoTracking().ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        // One transaction writes every row with the same time; the minimum is the honest one if that ever changes.
        return new UsageCounts(
            rows.Min(r => r.ComputedAt),
            [.. rows.Select(r => new ActiveUserCount(r.TenantSlug, r.Kind, r.TimeWindow, r.Users))]);
    }

    public async Task ReplaceAsync(UsageCounts counts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var slugs = counts.Counts.Select(c => c.TenantSlug).ToArray();
        var kinds = counts.Counts.Select(c => c.Kind).ToArray();
        var windows = counts.Counts.Select(c => c.Window).ToArray();
        var users = counts.Counts.Select(c => c.Users).ToArray();
        var computedAt = counts.ComputedAt.ToUniversalTime();

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"delete from ops.active_user_counts", cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"""
            insert into ops.active_user_counts (tenant_slug, kind, time_window, users, computed_at)
            select t.slug, t.kind, t.time_window, t.users, {computedAt}
            from unnest({slugs}::text[], {kinds}::text[], {windows}::text[], {users}::integer[]) as t(slug, kind, time_window, users)
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
