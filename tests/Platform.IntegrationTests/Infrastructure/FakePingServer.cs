using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// A TCP double for ClamAV (plan task 3): one instance answers <c>PONG</c> to any bytes it receives, the other
/// accepts the connection and never writes back, so the caller times out the way a stuck ClamAV would.
/// </summary>
internal sealed class FakePingServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    private FakePingServer(TcpListener listener, bool respond)
    {
        _listener = listener;
        _acceptLoop = AcceptLoopAsync(respond);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static FakePingServer StartResponding() => Start(respond: true);

    public static FakePingServer StartSilent() => Start(respond: false);

    private static FakePingServer Start(bool respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FakePingServer(listener, respond);
    }

    private async Task AcceptLoopAsync(bool respond)
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = respond ? RespondAsync(client) : HoldOpenAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
            // Test teardown: DisposeAsync cancelled the accept loop.
        }
        catch (ObjectDisposedException)
        {
            // Test teardown: the listener was stopped while a call to AcceptTcpClientAsync was pending.
        }
    }

    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[64];
                _ = await stream.ReadAsync(buffer, _cts.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("PONG\0"), _cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                // Test teardown while a client was mid-exchange: nothing more to do with a test double.
            }
        }
    }

    private async Task HoldOpenAsync(TcpClient client)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the connection is meant to stay open, unanswered, until the test tears it down.
        }
        finally
        {
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // The loop's own catches already handle teardown; nothing more to report from a test double.
        }

        _cts.Dispose();
    }
}
