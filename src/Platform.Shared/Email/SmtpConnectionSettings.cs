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
        var username = configuration["Smtp:Username"] is { Length: > 0 } user ? user : null;
        var password = configuration["Smtp:Password"] is { Length: > 0 } secret ? secret : null;
        if (username is not null && password is null)
        {
            throw new InvalidOperationException(
                "Smtp:Username is set but Smtp:Password is not; set the password in user secrets or the secret store.");
        }

        return new SmtpConnectionSettings(
            configuration["Smtp:Host"] ?? "localhost",
            int.TryParse(configuration["Smtp:Port"], out var port) ? port : 1025,
            security,
            username,
            password);
    }

    /// <summary>
    /// Connects and, when a username is configured, authenticates. With a login, <see cref="SmtpSecurity.Auto"/> means
    /// STARTTLS (implicit TLS on port 465) and is never downgraded to plain text; the login is refused on any connection
    /// that is not encrypted unless the server is on this machine (local test servers). A 20-second timeout bounds every step.
    /// </summary>
    public async Task ConnectAsync(SmtpClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = TimeoutMilliseconds;
        var security = Security == SmtpSecurity.Auto && UsesAuthentication
            ? (Port == 465 ? SmtpSecurity.Ssl : SmtpSecurity.StartTls)
            : Security;
        var options = security switch
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

            if (!client.IsSecure && !IsLocalHost(Host))
            {
                throw new AuthenticationException("Refusing to send the SMTP login over a connection that is not encrypted.");
            }

            await client.AuthenticateAsync(Username!, Password, cancellationToken);
        }
    }

    private const int TimeoutMilliseconds = 20_000;

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address));

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"Host = {Host}, Port = {Port}, Security = {Security}, Authenticated = {UsesAuthentication}");
        return true;
    }
}
