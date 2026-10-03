using System.Numerics;

namespace Platform.Shared.Telemetry;

/// <summary>
/// Number-typed values of ten or more digits (user ruling 2026-10-03, N-10 and PDPL; spec 7.1): a CR number, an iqama or a
/// national id logged as a <c>long</c>, <c>decimal</c> or <c>BigInteger</c> is as personal as the same digits in a string, so
/// the log enricher and the span processor replace it with <see cref="TelemetryRedactor.DigitsMarker"/>. An integral value
/// is long at an absolute value of 10^9 or more; a <c>float</c>, <c>double</c> or <c>decimal</c> when its integer part has ten
/// digits (<c>999999999.99</c> stays a number). A measurement is the exception, by an explicit allow-list of names
/// (<see cref="IsLogMeasurement"/>, <see cref="IsSpanMeasurement"/>): a byte count over 10 GB or a long duration is no
/// personal data, and a string where the backing index maps a number would make Elasticsearch refuse the record.
/// </summary>
internal static class NumericRedaction
{
    private const long Threshold = 1_000_000_000;

    /// <summary>Log property, structure member and dictionary key names that hold a measurement (compared ignoring case).</summary>
    private static readonly HashSet<string> LogMeasurements = new(StringComparer.OrdinalIgnoreCase)
    {
        "ElapsedMilliseconds", "ElapsedMs", "DurationMs", "DurationMilliseconds", "Duration", "Elapsed",
        "ContentLength", "Bytes", "ByteCount", "SizeBytes", "LengthBytes", "BodySize", "RequestBytes", "ResponseBytes",
        "RowCount", "Count", "Rows", "Size",
    };

    /// <summary>OpenTelemetry semantic-convention measurement tags and suffixes (compared ignoring case).</summary>
    private static readonly HashSet<string> SpanMeasurements = new(StringComparer.OrdinalIgnoreCase)
    {
        "http.request.size", "http.response.size", "http.request.body.size", "http.response.body.size",
        "db.response.returned_rows", "messaging.message.body.size",
    };

    private static readonly string[] SpanMeasurementSuffixes = [".duration", ".body.size", ".returned_rows", ".bytes"];

    public static bool IsLogMeasurement(string? name) => name is not null && LogMeasurements.Contains(name);

    public static bool IsSpanMeasurement(string name)
    {
        if (SpanMeasurements.Contains(name))
        {
            return true;
        }

        foreach (var suffix in SpanMeasurementSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for a number of ten or more digits (integer part for fractions); false for anything else.</summary>
    public static bool IsLongNumber(object? value) => value switch
    {
        sbyte or byte or short or ushort => false,
        int i => i is >= (int)Threshold or <= -(int)Threshold,
        uint u => u >= Threshold,
        long l => l is >= Threshold or <= -Threshold,
        ulong ul => ul >= Threshold,
        Int128 i128 => i128 >= Threshold || i128 <= -Threshold,
        UInt128 u128 => u128 >= Threshold,
        BigInteger big => big >= Threshold || big <= -Threshold,
        double d => double.IsFinite(d) && Math.Abs(d) >= Threshold,
        float f => float.IsFinite(f) && Math.Abs(f) >= Threshold,
        decimal m => Math.Abs(Math.Truncate(m)) >= Threshold,
        _ => false,
    };
}
