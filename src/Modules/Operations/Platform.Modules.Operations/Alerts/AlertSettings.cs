using Microsoft.Extensions.Configuration;

namespace Platform.Modules.Operations.Alerts;

/// <summary>
/// D-13/spec 3.3 configuration: <c>Smtp:Host</c>/<c>Smtp:Port</c> are shared with <see cref="Health.SmtpHealthCheck"/>;
/// <c>Smtp:From</c> and <c>Platform:AlertRecipients</c> (a list) are alert-only. No default recipient: with none
/// configured, <see cref="MailKitAlertSender"/> sends nothing rather than guessing an address (N-10: never invents a
/// destination for platform data).
/// </summary>
internal sealed record AlertSettings(
    string SmtpHost,
    int SmtpPort,
    string From,
    IReadOnlyList<string> Recipients,
    string BoardUrl)
{
    public static AlertSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new AlertSettings(
            configuration["Smtp:Host"] ?? "localhost",
            ParseInt(configuration["Smtp:Port"], 1025),
            configuration["Smtp:From"] ?? "no-reply@waslabid.test",
            configuration.GetSection("Platform:AlertRecipients").Get<string[]>() ?? [],
            // Platform:Host / a real console URL lands with Task 6; a plain default keeps the link in the email
            // meaningful in Development until then.
            configuration["Platform:BoardUrl"] ?? "http://localhost:5273/platform");
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;
}
