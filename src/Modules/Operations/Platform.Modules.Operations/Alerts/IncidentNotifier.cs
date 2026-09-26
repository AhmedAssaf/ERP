using Microsoft.EntityFrameworkCore;
using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations.Alerts;

/// <summary>
/// F-60: turns the <see cref="IncidentTransition"/>s from <see cref="IHealthLog.RecordAsync"/> into at most one
/// "is down" email and one "has recovered" email per incident. Durable via <c>notified_open</c>/<c>notified_close</c>
/// on the row (not the transient transition list) so re-running a health-check cycle, or a crash between recording
/// and notifying, never sends a duplicate.
/// </summary>
internal sealed class IncidentNotifier(IDbContextFactory<OperationsDbContext> contexts, IAlertSender sender, AlertSettings settings)
{
    public async Task NotifyAsync(IReadOnlyList<IncidentTransition> transitions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        if (transitions.Count == 0)
        {
            return;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var changed = false;

        foreach (var transition in transitions)
        {
            var incident = await db.Incidents.SingleAsync(i => i.Id == transition.IncidentId, cancellationToken);

            if (transition.Kind == IncidentTransitionKind.Opened && !incident.NotifiedOpen)
            {
                await sender.SendAsync(AlertMessages.IncidentOpened(incident, settings.BoardUrl), cancellationToken);
                incident.NotifiedOpen = true;
                changed = true;
            }
            else if (transition.Kind == IncidentTransitionKind.Closed && !incident.NotifiedClose)
            {
                await sender.SendAsync(AlertMessages.IncidentClosed(incident, settings.BoardUrl), cancellationToken);
                incident.NotifiedClose = true;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
