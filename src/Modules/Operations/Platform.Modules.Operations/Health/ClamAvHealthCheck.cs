using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Platform.Modules.Operations.Health;

/// <summary>Spec 3.2: the old-style null-terminated <c>zPING\0</c> command, expecting <c>PONG</c> back.</summary>
internal sealed class ClamAvHealthCheck(string host, int port) : IHealthCheck
{
    private const string Component = "ClamAV";
    private static readonly byte[] PingCommand = "zPING\0"u8.ToArray();

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            var stream = client.GetStream();
            await stream.WriteAsync(PingCommand, cancellationToken);

            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, cancellationToken);
            var reply = Encoding.ASCII.GetString(buffer, 0, read).TrimEnd('\0', '\r', '\n');
            return reply == "PONG"
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"{Component} did not answer PING.");
        }
        catch (Exception ex)
        {
            // Never ex.Message (N-10). Also catches the "never answers" case: the per-check timeout cancels the read.
            return HealthCheckResult.Unhealthy(
                HealthCheckMessages.WithExceptionType(ex, HealthCheckMessages.CouldNotReach(Component)));
        }
    }
}
