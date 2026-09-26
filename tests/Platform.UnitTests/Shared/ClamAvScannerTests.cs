using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Scanning;

namespace Platform.UnitTests.Shared;

/// <summary>
/// The ClamAV client (vendor plan task 3, V-10): <c>zINSTREAM\0</c>, then the content in chunks of at most 64 KB, each
/// after its 4-byte big-endian length, then a zero-length chunk. <c>OK</c> is clean, <c>... FOUND</c> infected with the
/// signature name; a scanner that cannot be reached, never answers or answers with an error is unavailable, never an
/// exception.
/// </summary>
public sealed class ClamAvScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Content_is_streamed_with_instream_framing_in_64_KB_chunks_and_a_zero_terminator()
    {
        var content = new byte[(2 * 65536) + 22528];
        Random.Shared.NextBytes(content);
        await using var server = FakeClamd.Start("stream: OK\0");

        var result = await Scanner(server.Port).ScanAsync(new MemoryStream(content), Ct);

        result.ShouldBe(ScanResult.Clean);
        var received = await server.ReceivedAsync();
        Encoding.ASCII.GetString(received, 0, 10).ShouldBe("zINSTREAM\0");
        var lengths = new List<int>();
        var payload = new List<byte>();
        var offset = 10;
        while (true)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(received.AsSpan(offset, 4));
            offset += 4;
            lengths.Add(length);
            if (length == 0)
            {
                break;
            }

            payload.AddRange(received.AsSpan(offset, length).ToArray());
            offset += length;
        }

        lengths.ShouldBe([65536, 65536, 22528, 0]);
        payload.ToArray().ShouldBe(content);
        offset.ShouldBe(received.Length);
    }

    [Fact]
    public async Task A_found_reply_is_infected_with_the_signature_name()
    {
        await using var server = FakeClamd.Start("stream: Eicar-Signature FOUND\0");

        var result = await Scanner(server.Port).ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        result.Verdict.ShouldBe(ScanVerdict.Infected);
        result.Signature.ShouldBe("Eicar-Signature");
    }

    [Theory]
    [InlineData("INSTREAM size limit exceeded. ERROR\0")]
    [InlineData("stream: Can't allocate memory ERROR\0")]
    [InlineData("")]
    public async Task An_error_or_empty_reply_is_unavailable(string reply)
    {
        await using var server = FakeClamd.Start(reply);

        var result = await Scanner(server.Port).ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        result.ShouldBe(ScanResult.Unavailable);
    }

    [Fact]
    public async Task A_refused_connection_is_unavailable()
    {
        var port = FakeClamd.UnusedPort();

        var result = await Scanner(port).ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        result.ShouldBe(ScanResult.Unavailable);
    }

    [Fact]
    public async Task A_scanner_that_never_answers_is_unavailable_after_the_timeout()
    {
        await using var server = FakeClamd.Start(reply: null);
        var scanner = Scanner(server.Port, TimeSpan.FromMilliseconds(500));

        var started = DateTime.UtcNow;
        var result = await scanner.ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        result.ShouldBe(ScanResult.Unavailable);
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Without_settings_every_scan_is_unavailable()
    {
        var scanner = new ClamAvScanner(ClamAvSettings.None, NullLogger<ClamAvScanner>.Instance);

        (await scanner.ScanAsync(new MemoryStream([1, 2, 3]), Ct)).ShouldBe(ScanResult.Unavailable);
    }

    [Fact]
    public async Task Content_over_the_stream_limit_is_refused_before_anything_is_sent()
    {
        await using var server = FakeClamd.Start("stream: OK\0");
        var scanner = new ClamAvScanner(
            new ClamAvSettings("127.0.0.1", server.Port, TimeSpan.FromSeconds(5), MaxStreamBytes: 1000), NullLogger<ClamAvScanner>.Instance);

        await Should.ThrowAsync<ArgumentException>(() => scanner.ScanAsync(new MemoryStream(new byte[1001]), Ct));
        server.Connections.ShouldBe(0);
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_swallowed()
    {
        await using var server = FakeClamd.Start(reply: null);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(
            () => Scanner(server.Port, TimeSpan.FromSeconds(30)).ScanAsync(new MemoryStream([1, 2, 3]), cancel.Token));
    }

    [Fact]
    public void Settings_come_from_the_health_check_keys_and_never_print_more_than_host_and_port()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClamAv:Host"] = "clamav.internal", ["ClamAv:Port"] = "3311" })
            .Build();

        var settings = ClamAvSettings.FromConfiguration(configuration);

        settings.Host.ShouldBe("clamav.internal");
        settings.Port.ShouldBe(3311);
        settings.IsConfigured.ShouldBeTrue();
        settings.MaxStreamBytes.ShouldBe(25L * 1024 * 1024);
        ClamAvSettings.FromConfiguration(new ConfigurationBuilder().Build()).IsConfigured.ShouldBeFalse();
    }

    private static ClamAvScanner Scanner(int port, TimeSpan? timeout = null) =>
        new(new ClamAvSettings("127.0.0.1", port, timeout ?? TimeSpan.FromSeconds(5)), NullLogger<ClamAvScanner>.Instance);

    /// <summary>A one-connection clamd stand-in: records what it receives until the zero chunk, then sends the reply (null: never).</summary>
    private sealed class FakeClamd : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<byte[]> _received;
        private readonly CancellationTokenSource _stop = new();

        private FakeClamd(TcpListener listener, string? reply)
        {
            _listener = listener;
            _received = Task.Run(() => ServeAsync(reply));
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Connections { get; private set; }

        public static FakeClamd Start(string? reply)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeClamd(listener, reply);
        }

        public static int UnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public Task<byte[]> ReceivedAsync() => _received;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _received;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
                // The test is over; a connection that never came or was cut is expected here.
            }

            _stop.Dispose();
        }

        private async Task<byte[]> ServeAsync(string? reply)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Connections++;
            var stream = client.GetStream();
            var received = new MemoryStream();
            var buffer = new byte[8192];
            while (!EndsWithZeroChunk(received.ToArray()))
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    break;
                }

                received.Write(buffer, 0, read);
            }

            if (reply is null)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply!), _stop.Token);
            client.Client.Shutdown(SocketShutdown.Send);
            return received.ToArray();
        }

        // After the 10-byte command: walk the length-prefixed chunks; true once the zero-length one arrived.
        private static bool EndsWithZeroChunk(byte[] data)
        {
            var offset = 10;
            while (offset + 4 <= data.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (length == 0)
                {
                    return true;
                }

                offset += length;
            }

            return false;
        }
    }
}
