using Microsoft.Extensions.Configuration;

namespace Platform.Shared.Email;

/// <summary>One plain-text email from the platform's own address (<c>Smtp:From</c>).</summary>
public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string TextBody);

/// <summary>
/// Sends the platform's own emails over SMTP (D-13: MailKit). Keycloak sends its account emails itself; this is for the
/// emails the application writes (platform alerts, staff notices). A failure to deliver throws
/// <see cref="EmailDeliveryException"/>.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>The SMTP server did not take the message. The message names no address and no server (N-10).</summary>
public sealed class EmailDeliveryException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>
/// <c>Smtp:Host</c>, <c>Smtp:Port</c> and <c>Smtp:From</c>; Development defaults to Mailpit on localhost:1025 (the
/// Compose stack).
/// </summary>
public sealed record EmailSettings(string SmtpHost, int SmtpPort, string From)
{
    public static EmailSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new EmailSettings(
            configuration["Smtp:Host"] ?? "localhost",
            int.TryParse(configuration["Smtp:Port"], out var port) ? port : 1025,
            configuration["Smtp:From"] ?? "no-reply@waslabid.test");
    }
}
