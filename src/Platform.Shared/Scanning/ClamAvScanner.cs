using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Scanning;

/// <summary>
/// <see cref="IVirusScanner"/> over clamd's TCP <c>INSTREAM</c> command: <c>zINSTREAM\0</c>, then the content in chunks
/// of at most 64 KB, each after its length as 4 bytes big-endian, then a zero length; clamd answers
/// <c>stream: OK</c>, <c>stream: {signature} FOUND</c> or an error, ending with a NUL. A connection failure, a timeout
/// (<see cref="ClamAvSettings.Timeout"/> for the whole exchange) or an error answer is
/// <see cref="ScanResult.Unavailable"/> and logged with the exception type only (N-10). Content longer than
/// <see cref="ClamAvSettings.MaxStreamBytes"/> is a caller's defect: clamd would cut it off and answer with an error, so
/// it is refused before anything is sent.
/// </summary>
public sealed partial class ClamAvScanner(ClamAvSettings settings, ILogger<ClamAvScanner> logger) : IVirusScanner
{
    internal const int ChunkBytes = 64 * 1024;

    private static readonly byte[] Command = "zINSTREAM\0"u8.ToArray();

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.CanSeek && content.Length - content.Position > settings.MaxStreamBytes)
        {
            throw new ArgumentException($"The content is longer than the scanner's limit of {settings.MaxStreamBytes} bytes.", nameof(content));
        }

        if (!settings.IsConfigured)
        {
            LogNotConfigured(logger);
            return ScanResult.Unavailable;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            var reply = await ExchangeAsync(content, timeout.Token);
            return Interpret(reply);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnavailable(logger, "Timeout");
            return ScanResult.Unavailable;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            LogUnavailable(logger, ex.GetType().Name);
            return ScanResult.Unavailable;
        }
    }

    private async Task<string> ExchangeAsync(Stream content, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(settings.Host!, settings.Port, cancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(Command, cancellationToken);

        var buffer = ArrayPool<byte>.Shared.Rent(4 + ChunkBytes);
        try
        {
            long sent = 0;
            while (true)
            {
                var read = await content.ReadAtLeastAsync(buffer.AsMemory(4, ChunkBytes), ChunkBytes, throwOnEndOfStream: false, cancellationToken);
                sent += read;
                if (sent > settings.MaxStreamBytes)
                {
                    throw new ArgumentException($"The content is longer than the scanner's limit of {settings.MaxStreamBytes} bytes.", nameof(content));
                }

                BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), read);
                await stream.WriteAsync(buffer.AsMemory(0, 4 + read), cancellationToken);
                if (read == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // The answer is short and ends with NUL; clamd closes the connection after it.
        var reply = new MemoryStream();
        var answer = new byte[256];
        while (reply.Length < 4096)
        {
            var read = await stream.ReadAsync(answer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            reply.Write(answer, 0, read);
            if (answer[read - 1] == 0)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(reply.GetBuffer(), 0, (int)reply.Length).TrimEnd('\0', '\n', '\r');
    }

    private ScanResult Interpret(string reply)
    {
        const string prefix = "stream: ";
        const string found = " FOUND";
        if (reply == prefix + "OK")
        {
            return ScanResult.Clean;
        }

        if (reply.StartsWith(prefix, StringComparison.Ordinal) && reply.EndsWith(found, StringComparison.Ordinal)
            && reply.Length > prefix.Length + found.Length)
        {
            return ScanResult.Infected(reply[prefix.Length..^found.Length]);
        }

        // An error answer (size limit, memory) or none at all: no verdict. The reply text is clamd's own, not ours.
        LogUnavailable(logger, reply.Length == 0 ? "EmptyReply" : "ErrorReply");
        return ScanResult.Unavailable;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ClamAV is not configured (ClamAv:Host); the content stays unscanned.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ClamAV gave no verdict ({Reason}); the content stays unscanned for a later retry.")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
