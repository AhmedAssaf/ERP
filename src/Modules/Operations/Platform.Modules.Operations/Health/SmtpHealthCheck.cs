using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>Spec 3.2: connect and <c>NOOP</c>.</summary>
internal sealed class SmtpHealthCheck(string host, int port) : IHealthCheck
{
    private const string Component = "SMTP";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(host, port, SecureSocketOptions.Auto, cancellationToken);
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
