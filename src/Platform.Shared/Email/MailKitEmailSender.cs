using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Platform.Shared.Email;

/// <summary>D-13: MailKit over the obsolete <c>System.Net.Mail.SmtpClient</c>; one connection per message.</summary>
public sealed class MailKitEmailSender(EmailSettings settings) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.To.Count == 0)
        {
            throw new ArgumentException("An email needs at least one recipient.", nameof(message));
        }

        using var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(settings.From));
        foreach (var recipient in message.To)
        {
            mime.To.Add(MailboxAddress.Parse(recipient));
        }

        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.TextBody };

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, SecureSocketOptions.Auto, cancellationToken);
            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or CommandException or ProtocolException
            or ServiceNotConnectedException or SslHandshakeException or AuthenticationException)
        {
            throw new EmailDeliveryException($"The SMTP server did not take the email ({ex.GetType().Name}).", ex);
        }
    }
}
