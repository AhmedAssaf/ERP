using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Platform.Modules.Operations.Alerts;

/// <summary>
/// F-60: at most one "is down" email and one "has recovered" email per incident, and never a lost one. Driven by the
/// durable <c>notified_open</c>/<c>notified_close</c> flags rather than the transient transitions of one run: every
/// run sends for each open incident not yet announced and each incident closed in the last 24 hours whose recovery
/// was not yet announced. Each flag is saved right after its own successful send, so a crash or a failed send later
/// in the run never causes a duplicate; a send that throws is logged (exception type only, N-10) and retried on the
/// next run while the others still go out.
/// </summary>
internal sealed partial class IncidentNotifier(
    IDbContextFactory<OperationsDbContext> contexts,
    IAlertSender sender,
    AlertSettings settings,
    TimeProvider clock,
    ILogger<IncidentNotifier> logger)
{
    /// <summary>A recovery notice older than this is no longer worth sending late.</summary>
    internal static readonly TimeSpan RecoveryResendWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Marks the open, not yet announced incidents of components the fallback path (see
    /// <see cref="FallbackAlertState"/>) already emailed "is down" for as announced, without sending, so the store
    /// coming back does not repeat an email the admin already has. Keyed by component rather than by this run's
    /// transitions, so a run that opened the incident but failed before marking it is still covered next time.
    /// </summary>
    public async Task MarkOpenAlreadyAnnouncedAsync(
        IReadOnlySet<string> announcedComponents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(announcedComponents);
        if (announcedComponents.Count == 0)
        {
            return;
        }

        var components = announcedComponents.ToList();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Incidents
            .Where(i => i.ClosedAt == null && !i.NotifiedOpen && components.Contains(i.Component))
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.NotifiedOpen, true), cancellationToken);
    }

    /// <summary>Sends every pending incident email (see the class remarks), oldest incident first.</summary>
    public async Task NotifyPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var recoveredSince = clock.GetUtcNow() - RecoveryResendWindow;

        var pending = await db.Incidents
            .Where(i => (i.ClosedAt == null && !i.NotifiedOpen) ||
                        (i.ClosedAt != null && !i.NotifiedClose && i.ClosedAt >= recoveredSince))
            .OrderBy(i => i.OpenedAt)
            .ToListAsync(cancellationToken);

        foreach (var incident in pending)
        {
            var recovered = incident.ClosedAt is not null;
            var message = recovered
                ? AlertMessages.IncidentClosed(incident, settings.BoardUrl)
                : AlertMessages.IncidentOpened(incident, settings.BoardUrl);

            try
            {
                await sender.SendAsync(message, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogSendFailed(logger, incident.Component, recovered ? "recovery" : "down", ex.GetType().Name);
                continue;
            }

            if (recovered)
            {
                incident.NotifiedClose = true;
            }
            else
            {
                incident.NotifiedOpen = true;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The {Kind} alert for {Component} could not be sent ({ErrorType}); it is retried on the next health-check run.")]
    private static partial void LogSendFailed(ILogger logger, string component, string kind, string errorType);
}
