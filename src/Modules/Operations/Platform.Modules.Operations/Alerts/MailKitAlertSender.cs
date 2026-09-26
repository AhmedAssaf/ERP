using Platform.Shared.Email;

namespace Platform.Modules.Operations.Alerts;

/// <summary>
/// Platform alerts (F-60) through the shared MailKit sender (D-13) to <c>Platform:AlertRecipients</c>, from
/// <c>Smtp:From</c> on <c>Smtp:Host</c>/<c>Smtp:Port</c>.
/// </summary>
internal sealed class MailKitAlertSender(AlertSettings settings) : IAlertSender
{
    private readonly MailKitEmailSender _email = new(new EmailSettings(settings.SmtpHost, settings.SmtpPort, settings.From));

    public async Task SendAsync(AlertMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (settings.Recipients.Count == 0)
        {
            // No Platform:AlertRecipients configured: nothing to do (see AlertSettings remarks).
            return;
        }

        await _email.SendAsync(new EmailMessage(settings.Recipients, message.Subject, message.Body), cancellationToken);
    }
}
