namespace Platform.Modules.Operations.Contracts;

/// <summary>
/// One alert email to the platform admins (F-60 path: <c>Platform:AlertRecipients</c> through the shared SMTP settings),
/// for another module's event that needs a platform admin's attention (W-33: a new CR ownership dispute). The text must
/// carry no personal data and no secret (N-10): it says what happened and where to look, never who.
/// </summary>
public sealed record PlatformAlert(string Subject, string Body);

/// <summary>
/// Sends <see cref="PlatformAlert"/>s. Registered where the F-60 alerts are (the worker); with no recipients configured
/// nothing is sent. A failure surfaces as the sender's exception, so the calling job retries on its next run.
/// </summary>
public interface IPlatformAlerts
{
    Task SendAsync(PlatformAlert alert, CancellationToken cancellationToken = default);
}
