using System.Buffers.Binary;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>One span as the OTLP exporter wrote it on the wire: what reached the collector, not what the activity held.</summary>
internal sealed record ExportedSpan(string TraceId, string Name, int Events, int DroppedEvents, IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// A fake OTLP gRPC trace receiver (W-10 follow-up, fix round 1): a <see cref="FakeHttpServer"/> on cleartext HTTP/2 that
/// takes <c>TraceService/Export</c> calls, answers gRPC OK, and decodes each <c>ExportTraceServiceRequest</c> with a minimal
/// protobuf reader (only the fields the tests read: span trace id, name, attributes with string values, events and the
/// dropped events count). So a test sees exactly what the hosts' OTLP exporter sends to the collector.
/// </summary>
internal sealed class OtlpTraceReceiver : IAsyncDisposable
{
    private readonly List<ExportedSpan> _spans = [];
    private FakeHttpServer? _server;

    /// <summary>The endpoint to give the host as <c>Telemetry:OtlpEndpoint</c>.</summary>
    public string Endpoint => _server!.BaseAddress.TrimEnd('/');

    public static async Task<OtlpTraceReceiver> StartAsync(CancellationToken cancellationToken)
    {
        var receiver = new OtlpTraceReceiver();
        receiver._server = await FakeHttpServer.StartAsync(receiver.HandleAsync, cancellationToken, http2Only: true);
        return receiver;
    }

    /// <summary>Every span received so far.</summary>
    public IReadOnlyList<ExportedSpan> Spans
    {
        get
        {
            lock (_spans)
            {
                return [.. _spans];
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);
        if (context.Request.Path.Value?.EndsWith(".TraceService/Export", StringComparison.Ordinal) == true)
        {
            var spans = Decode(body.ToArray());
            lock (_spans)
            {
                _spans.AddRange(spans);
            }
        }

        context.Response.ContentType = "application/grpc";
        // An empty ExportTraceServiceResponse in one uncompressed gRPC frame, then status OK.
        await context.Response.Body.WriteAsync(new byte[5]);
        context.Response.AppendTrailer("grpc-status", "0");
    }

    /// <summary>The spans of every gRPC frame in a request body (a 5-byte prefix: compressed flag and big-endian length).</summary>
    private static List<ExportedSpan> Decode(byte[] body)
    {
        var spans = new List<ExportedSpan>();
        var offset = 0;
        while (offset + 5 <= body.Length)
        {
            if (body[offset] != 0)
            {
                throw new InvalidOperationException("The test receiver reads uncompressed OTLP only.");
            }

            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(offset + 1, 4));
            foreach (var resourceSpans in Fields(body.AsMemory(offset + 5, length), 1))
            {
                foreach (var scopeSpans in Fields(resourceSpans, 2))
                {
                    foreach (var span in Fields(scopeSpans, 2))
                    {
                        spans.Add(Span(span));
                    }
                }
            }

            offset += 5 + length;
        }

        return spans;
    }

    private static ExportedSpan Span(ReadOnlyMemory<byte> span)
    {
        string traceId = string.Empty, name = string.Empty;
        int events = 0, dropped = 0;
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, _, value, number) in Read(span))
        {
            switch (field)
            {
                case 1:
                    traceId = Convert.ToHexStringLower(value.Span);
                    break;
                case 5:
                    name = Encoding.UTF8.GetString(value.Span);
                    break;
                case 9:
                    var key = Fields(value, 1).Select(k => Encoding.UTF8.GetString(k.Span)).FirstOrDefault() ?? string.Empty;
                    var text = Fields(value, 2).SelectMany(v => Fields(v, 1)).Select(v => Encoding.UTF8.GetString(v.Span)).FirstOrDefault();
                    if (text is not null)
                    {
                        attributes[key] = text;
                    }

                    break;
                case 11:
                    events++;
                    break;
                case 12:
                    dropped = (int)number;
                    break;
            }
        }

        return new ExportedSpan(traceId, name, events, dropped, attributes);
    }

    /// <summary>The length-delimited values of one field number in a message.</summary>
    private static List<ReadOnlyMemory<byte>> Fields(ReadOnlyMemory<byte> message, int field) =>
        Read(message).Where(f => f.Field == field && f.Wire == 2).Select(f => f.Value).ToList();

    /// <summary>Every field of a message: number, wire type, the bytes of a length-delimited value, the number of a varint.</summary>
    private static List<(int Field, int Wire, ReadOnlyMemory<byte> Value, ulong Number)> Read(ReadOnlyMemory<byte> message)
    {
        var fields = new List<(int, int, ReadOnlyMemory<byte>, ulong)>();
        var bytes = message.Span;
        var position = 0;
        while (position < bytes.Length)
        {
            var tag = Varint(bytes, ref position);
            var field = (int)(tag >> 3);
            switch ((int)(tag & 7))
            {
                case 0:
                    fields.Add((field, 0, ReadOnlyMemory<byte>.Empty, Varint(bytes, ref position)));
                    break;
                case 1:
                    position += 8;
                    break;
                case 2:
                    var length = (int)Varint(bytes, ref position);
                    fields.Add((field, 2, message.Slice(position, length), 0));
                    position += length;
                    break;
                case 5:
                    position += 4;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected protobuf wire type in field {field}.");
            }
        }

        return fields;
    }

    private static ulong Varint(ReadOnlySpan<byte> bytes, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = bytes[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }
    }
}
