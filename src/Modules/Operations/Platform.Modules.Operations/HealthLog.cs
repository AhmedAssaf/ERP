using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations;

internal sealed class HealthLog(IDbContextFactory<OperationsDbContext> contexts) : IHealthLog
{
    private const string OneOpenIncidentPerComponentConstraint = "ux_incidents_one_open_per_component";

    public async Task<IReadOnlyList<IncidentTransition>> RecordAsync(
        IReadOnlyList<HealthResult> results, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        // The raw results are saved on their own, before any incident handling (task 3): a concurrent writer racing
        // to open the same incident must never cost us the health_results rows.
        foreach (var result in results)
        {
            db.HealthResults.Add(new HealthResultRow
            {
                Id = Guid.CreateVersion7(),
                Component = result.Component,
                Status = result.Status,
                LatencyMs = result.LatencyMs,
                CheckedAt = result.CheckedAt,
                Message = result.Message,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        var transitions = new List<IncidentTransition>();
        foreach (var result in results)
        {
            var transition = await RecordIncidentAsync(db, result, cancellationToken);
            if (transition is not null)
            {
                transitions.Add(transition);
            }
        }

        return transitions;
    }

    private static async Task<IncidentTransition?> RecordIncidentAsync(
        OperationsDbContext db, HealthResult result, CancellationToken cancellationToken)
    {
        var openIncident = await db.Incidents
            .Where(i => i.Component == result.Component && i.ClosedAt == null)
            .SingleOrDefaultAsync(cancellationToken);

        switch (IncidentRule.Evaluate(result.Status, openIncident is not null))
        {
            case IncidentRule.Outcome.Open:
                var opened = new IncidentRow
                {
                    Id = Guid.CreateVersion7(),
                    Component = result.Component,
                    OpenedAt = result.CheckedAt,
                    LastMessage = result.Message,
                };
                db.Incidents.Add(opened);

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (IsOpenIncidentConflict(ex))
                {
                    // Another concurrent check run opened it first between our read and our write (task 3): drop our
                    // losing insert, adopt the winner's incident, and continue without throwing or opening a second
                    // one. No transition is returned here; the winning call already reported the Opened one.
                    db.Entry(opened).State = EntityState.Detached;
                    var existing = await db.Incidents
                        .Where(i => i.Component == result.Component && i.ClosedAt == null)
                        .SingleAsync(cancellationToken);
                    existing.LastMessage = result.Message;
                    await db.SaveChangesAsync(cancellationToken);
                    return null;
                }

                return new IncidentTransition(IncidentTransitionKind.Opened, result.Component, opened.Id);

            case IncidentRule.Outcome.Close:
                openIncident!.ClosedAt = result.CheckedAt;
                // last_message keeps the failure text (task 4): the healthy result carries no message to overwrite
                // it with, so it is left untouched.
                await db.SaveChangesAsync(cancellationToken);
                return new IncidentTransition(IncidentTransitionKind.Closed, result.Component, openIncident.Id);

            case IncidentRule.Outcome.None:
                if (openIncident is not null)
                {
                    // Repeated failure while already open: keep the incident's latest failure text current.
                    openIncident.LastMessage = result.Message;
                    await db.SaveChangesAsync(cancellationToken);
                }

                return null;

            default:
                return null;
        }
    }

    private static bool IsOpenIncidentConflict(DbUpdateException exception)
    {
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
                pg.ConstraintName == OneOpenIncidentPerComponentConstraint)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<HealthResult>> LatestAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        // Correlated subquery on MAX(checked_at) per component: translates to plain SQL on every relational provider,
        // unlike GroupBy().Select(g => g.OrderBy(...).First()), which not every provider turns into one query.
        var rows = await db.HealthResults
            .Where(h => h.CheckedAt == db.HealthResults
                .Where(x => x.Component == h.Component)
                .Max(x => (DateTimeOffset?)x.CheckedAt))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToHealthResult)];
    }

    public async Task<IReadOnlyList<HealthResult>> LastFailuresAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        // Same shape as LatestAsync, restricted to failing rows on both sides.
        var rows = await db.HealthResults
            .Where(h => h.Status != HealthStatus.Healthy)
            .Where(h => h.CheckedAt == db.HealthResults
                .Where(x => x.Component == h.Component && x.Status != HealthStatus.Healthy)
                .Max(x => (DateTimeOffset?)x.CheckedAt))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToHealthResult)];
    }

    public async Task<IReadOnlyList<Incident>> IncidentsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var rows = await db.Incidents
            .Where(i => i.OpenedAt >= since)
            .OrderByDescending(i => i.OpenedAt)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToIncident)];
    }

    private static HealthResult ToHealthResult(HealthResultRow row) =>
        new(row.Component, row.Status, row.LatencyMs, row.CheckedAt, row.Message);

    private static Incident ToIncident(IncidentRow row) =>
        new(row.Id, row.Component, row.OpenedAt, row.ClosedAt, row.LastMessage);
}
