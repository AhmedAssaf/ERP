using Microsoft.EntityFrameworkCore;
using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations;

internal sealed class HealthLog(IDbContextFactory<OperationsDbContext> contexts) : IHealthLog
{
    public async Task<IReadOnlyList<IncidentTransition>> RecordAsync(
        IReadOnlyList<HealthResult> results, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);

        var transitions = new List<IncidentTransition>();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

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
                    transitions.Add(new IncidentTransition(IncidentTransitionKind.Opened, result.Component, opened.Id));
                    break;

                case IncidentRule.Outcome.Close:
                    openIncident!.ClosedAt = result.CheckedAt;
                    openIncident.LastMessage = result.Message;
                    transitions.Add(new IncidentTransition(IncidentTransitionKind.Closed, result.Component, openIncident.Id));
                    break;

                case IncidentRule.Outcome.None:
                    if (openIncident is not null)
                    {
                        // Repeated failure while already open: keep the incident's latest failure text current.
                        openIncident.LastMessage = result.Message;
                    }

                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return transitions;
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
