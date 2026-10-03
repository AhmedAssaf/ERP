using MailKit.Net.Smtp;
using Platform.Shared.Email;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>Spec 3.2: connect (log in when <c>Smtp:Username</c> is set) and <c>NOOP</c>.</summary>
internal sealed class SmtpHealthCheck(SmtpConnectionSettings settings) : IHealthCheck
{
    private const string Component = "SMTP";

    public SmtpHealthCheck(string host, int port)
        : this(new SmtpConnectionSettings(host, port))
    {
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new SmtpClient();
            await settings.ConnectAsync(client, cancellationToken);
            await client.NoOpAsync(cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10): an SmtpCommandException can echo server banners or auth state.
            return HealthCheckResult.Unhealthy(
                HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach(Component)));
        }
    }
}
