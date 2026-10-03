using System.Net;
using System.Net.Sockets;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// W-34: a loopback TCP proxy that forwards to an upstream server until <see cref="Hang"/> is called, after which it keeps
/// every connection open but passes no byte in either direction: a server that accepted the connection and stopped
/// answering. Without an upstream it never answers at all (accepts and stays silent).
/// </summary>
internal sealed class HangingTcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _clients = [];
    private readonly string? _upstreamHost;
    private readonly int _upstreamPort;
    private readonly Task _accepting;
    private volatile bool _hanging;

    private HangingTcpProxy(string? upstreamHost, int upstreamPort)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Forwards to <paramref name="upstream"/> (<c>host:port</c>) until <see cref="Hang"/>.</summary>
    public static HangingTcpProxy To(string upstream)
    {
        var separator = upstream.LastIndexOf(':');
        return new HangingTcpProxy(upstream[..separator], int.Parse(upstream[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Accepts every connection and never sends a byte.</summary>
    public static HangingTcpProxy Silent() => new(null, 0);

    /// <summary>From now on no byte passes in either direction; connections stay open.</summary>
    public void Hang() => _hanging = true;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        lock (_clients)
        {
            _clients.ForEach(c => c.Dispose());
        }

        try
        {
            await _accepting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The listener was stopped on purpose.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Track(client);
            if (_upstreamHost is null)
            {
                continue;
            }

            var upstream = new TcpClient();
            Track(upstream);
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, _stop.Token);
            _ = PumpAsync(client.GetStream(), upstream.GetStream());
            _ = PumpAsync(upstream.GetStream(), client.GetStream());
        }
    }

    private void Track(TcpClient client)
    {
        lock (_clients)
        {
            _clients.Add(client);
        }
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                if (!_hanging)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
            // The proxy or one side closed; the other side notices on its own.
        }
    }
}
