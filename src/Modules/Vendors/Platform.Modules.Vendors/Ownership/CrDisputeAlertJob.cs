using Hangfire;
using Microsoft.EntityFrameworkCore;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors.Persistence;

namespace Platform.Modules.Vendors.Ownership;

/// <summary>
/// The recurring job "vendor-dispute-alert" (W-33, ADR-0013 decision 1), every five minutes in the worker: tells the
/// platform admins that new CR ownership disputes wait for triage, through the F-60 alert path
/// (<see cref="IPlatformAlerts"/>). One email per run for every dispute not announced yet, with their number and where to
/// look, never who raised them or about which company (N-10). The disputes are marked announced only after the email went
/// out (<c>vendor.mark_cr_disputes_alerted</c>), so a failed send is retried on the next run: at least once, never lost.
/// Reads through <c>vendor.unalerted_cr_disputes</c>, which answers only a session with neither a tenant nor a vendor
/// context nor an acting user (the worker).
/// </summary>
internal sealed class CrDisputeAlertJob(IDbContextFactory<VendorsDbContext> contexts, IPlatformAlerts alerts)
{
    private const int BatchSize = 200;

    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<Guid> pending;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            pending = await db.Database.SqlQuery<Guid>($"select id as \"Value\" from vendor.unalerted_cr_disputes({BatchSize})")
                .ToListAsync(cancellationToken);
        }

        if (pending.Count == 0)
        {
            return;
        }

        await alerts.SendAsync(
            new PlatformAlert(
                pending.Count == 1 ? "WaslaBid: a new CR ownership dispute waits for review" : $"WaslaBid: {pending.Count} new CR ownership disputes wait for review",
                "Someone says a company registered on WaslaBid belongs to them. Open the platform console, Vendor ownership, to "
                + "accept the request for review, uphold it or reject it. A request holds nothing for a verified company until it is "
                + "accepted for review."),
            cancellationToken);

        await using var marking = await contexts.CreateDbContextAsync(cancellationToken);
        var ids = pending.ToArray();
        await marking.Database.SqlQuery<int>($"select vendor.mark_cr_disputes_alerted({ids}) as \"Value\"").SingleAsync(cancellationToken);
    }
}
