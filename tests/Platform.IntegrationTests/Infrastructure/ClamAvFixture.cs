using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// ClamAV (the <c>clamav/clamav:stable</c> image of infra/compose) for the vendor document tests (vendor plan task 3).
/// The signature mirror is not contacted: <c>CLAMAV_NO_FRESHCLAMD</c> starts clamd on the database bundled in the image,
/// so start-up takes about half a minute and does not depend on the network. Ready once clamd answers <c>PING</c>.
/// </summary>
public sealed class ClamAvFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);

    private readonly IContainer _container = new ContainerBuilder("clamav/clamav:stable")
        .WithEnvironment("CLAMAV_NO_FRESHCLAMD", "true")
        .WithPortBinding(3310, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("clamd started", o => o.WithTimeout(StartTimeout)))
        .Build();

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(3310);

    /// <summary>The hosts' <c>ClamAv:*</c> settings for this container.</summary>
    public IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["ClamAv:Host"] = Host,
        ["ClamAv:Port"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await WaitForPongAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    // The log line comes as clamd opens its socket; the mapped port can lag a moment behind it.
    private async Task WaitForPongAsync()
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(Host, Port);
                var stream = client.GetStream();
                await stream.WriteAsync("zPING\0"u8.ToArray());
                var buffer = new byte[16];
                var read = await stream.ReadAsync(buffer);
                if (Encoding.ASCII.GetString(buffer, 0, read).TrimEnd('\0') == "PONG")
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException && DateTime.UtcNow < deadline)
            {
                // Not listening yet.
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("ClamAV did not answer PING within the start-up timeout.");
            }

            await Task.Delay(500);
        }
    }
}
