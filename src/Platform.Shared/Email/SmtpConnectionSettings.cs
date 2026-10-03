using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;

namespace Platform.Shared.Email;

/// <summary>How the connection to the SMTP server is secured (<c>Smtp:Security</c>).</summary>
public enum SmtpSecurity
{
    /// <summary>MailKit decides from the port and the server's capabilities (the default; Mailpit stays plain).</summary>
    Auto,

    /// <summary>No TLS (local Mailpit).</summary>
    None,

    /// <summary>Upgrade with STARTTLS and fail if the server does not offer it (port 587, Oracle Email Delivery).</summary>
    StartTls,

    /// <summary>TLS from the first byte (implicit TLS, port 465).</summary>
    Ssl,
}

/// <summary>
/// Where and how to reach the SMTP server: <c>Smtp:Host</c>, <c>Smtp:Port</c>, <c>Smtp:Security</c> (Auto, None,
/// StartTls, Ssl), and the optional login <c>Smtp:Username</c> / <c>Smtp:Password</c>. With no username no login is
/// attempted. The password comes only from user secrets or the secret store (N-10) and is never part of
/// <see cref="ToString"/>, a log line or an exception message.
/// </summary>
public sealed record SmtpConnectionSettings(
    string Host,
    int Port,
    SmtpSecurity Security = SmtpSecurity.Auto,
    string? Username = null,
    string? Password = null)
{
    public bool UsesAuthentication => !string.IsNullOrEmpty(Username);

    public static SmtpConnectionSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var security = Enum.TryParse<SmtpSecurity>(configuration["Smtp:Security"], ignoreCase: true, out var parsed)
            ? parsed
            : SmtpSecurity.Auto;
        return new SmtpConnectionSettings(
            configuration["Smtp:Host"] ?? "localhost",
            int.TryParse(configuration["Smtp:Port"], out var port) ? port : 1025,
            security,
            configuration["Smtp:Username"] is { Length: > 0 } user ? user : null,
            configuration["Smtp:Password"] is { Length: > 0 } password ? password : null);
    }

    /// <summary>Connects and, when a username is configured, authenticates.</summary>
    public async Task ConnectAsync(SmtpClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var options = Security switch
        {
            SmtpSecurity.None => SecureSocketOptions.None,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.Ssl => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.Auto,
        };
        await client.ConnectAsync(Host, Port, options, cancellationToken);
        if (UsesAuthentication)
        {
            if (string.IsNullOrEmpty(Password))
            {
                throw new InvalidOperationException("Smtp:Username is set but Smtp:Password is not.");
            }

            await client.AuthenticateAsync(Username!, Password, cancellationToken);
        }
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"Host = {Host}, Port = {Port}, Security = {Security}, Authenticated = {UsesAuthentication}");
        return true;
    }
}
