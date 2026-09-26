using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Platform.Modules.Operations.Alerts;

/// <summary>D-13: MailKit over the obsolete <c>System.Net.Mail.SmtpClient</c>.</summary>
internal sealed class MailKitAlertSender(AlertSettings settings) : IAlertSender
{
    public async Task SendAsync(AlertMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (settings.Recipients.Count == 0)
        {
            // No Platform:AlertRecipients configured: nothing to do (see AlertSettings remarks).
            return;
        }

        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(settings.From));
        foreach (var recipient in settings.Recipients)
        {
            mime.To.Add(MailboxAddress.Parse(recipient));
        }

        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, SecureSocketOptions.Auto, cancellationToken);
        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
