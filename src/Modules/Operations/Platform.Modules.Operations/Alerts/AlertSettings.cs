using Microsoft.Extensions.Configuration;
using Platform.Shared.Email;

namespace Platform.Modules.Operations.Alerts;

/// <summary>
/// D-13/spec 3.3 configuration: <c>Smtp:*</c> (host, port, security, login) is shared with <see cref="Health.SmtpHealthCheck"/>;
/// <c>Smtp:From</c> and <c>Platform:AlertRecipients</c> (a list) are alert-only. No default recipient: with none
/// configured, <see cref="MailKitAlertSender"/> sends nothing rather than guessing an address (N-10: never invents a
/// destination for platform data).
/// </summary>
internal sealed record AlertSettings(
    SmtpConnectionSettings Smtp,
    string From,
    IReadOnlyList<string> Recipients,
    string BoardUrl)
{
    public static AlertSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new AlertSettings(
            SmtpConnectionSettings.FromConfiguration(configuration),
            configuration["Smtp:From"] ?? "no-reply@waslabid.test",
            configuration.GetSection("Platform:AlertRecipients").Get<string[]>() ?? [],
            // Platform:Host / a real console URL lands with Task 6; a plain default keeps the link in the email
            // meaningful in Development until then.
            configuration["Platform:BoardUrl"] ?? "http://localhost:5273/platform");
    }

}
