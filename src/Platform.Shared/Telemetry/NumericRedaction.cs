using System.Numerics;

namespace Platform.Shared.Telemetry;

/// <summary>
/// Number-typed values of ten or more digits (user ruling 2026-10-03, N-10 and PDPL; spec 7.1): a CR number, an iqama or a
/// national id logged as a <c>long</c>, <c>decimal</c> or <c>BigInteger</c> is as personal as the same digits in a string, so
/// the log enricher and the span processor replace it with <see cref="TelemetryRedactor.DigitsMarker"/>. An integral value
/// is long at an absolute value of 10^9 or more; a <c>float</c>, <c>double</c> or <c>decimal</c> when its integer part has ten
/// digits (<c>999999999.99</c> stays a number). A measurement is the exception, by an explicit allow-list of names
/// (<see cref="IsLogMeasurement"/>, <see cref="IsSpanMeasurement"/>): a byte count over 10 GB or a long duration is no
/// personal data, and a masked measurement would no longer be searchable or aggregatable as a number (the otel data streams set <c>index.mapping.ignore_malformed</c>, so a string in a numeric field is not refused: it stays in _source and the field is listed in _ignored).
/// </summary>
internal static class NumericRedaction
{
    private const long Threshold = 1_000_000_000;

    /// <summary>Log property, structure member and dictionary key names that are a measurement, compared whole and ignoring case.</summary>
    private static readonly HashSet<string> LogMeasurements = new(StringComparer.OrdinalIgnoreCase)
    {
        "ElapsedMilliseconds", "ElapsedMs", "DurationMs", "Duration", "Elapsed", "Ms", "ContentLength", "Bytes", "Count", "Rows",
        "RowsAffected", "Size", "Seconds",
    };

    /// <summary>
    /// Name endings of a measurement (<c>TotalBytes</c>, <c>RowsAffected</c>): PascalCase or camelCase endings compared exactly,
    /// so <c>Claims</c> or <c>Items</c> do not end in <c>Ms</c>; the snake_case forms (<c>total_bytes</c>) and an all-lower-case name
    /// (<c>sizebytes</c>, any ending but <c>ms</c>) ignoring case.
    /// </summary>
    private static readonly string[] LogMeasurementSuffixes = ["Bytes", "Count", "Duration", "Ms", "Seconds", "Size", "RowsAffected", "Milliseconds"];

    /// <summary>OpenTelemetry semantic-convention measurement tags and suffixes (compared ignoring case).</summary>
    private static readonly HashSet<string> SpanMeasurements = new(StringComparer.OrdinalIgnoreCase)
    {
        "http.request.size", "http.response.size", "http.request.body.size", "http.response.body.size",
        "db.response.returned_rows", "messaging.message.body.size",
    };

    private static readonly string[] SpanMeasurementSuffixes = [".duration", ".body.size", ".returned_rows", ".bytes"];

    public static bool IsLogMeasurement(string? name)
    {
        if (name is null)
        {
            return false;
        }

        if (LogMeasurements.Contains(name))
        {
            return true;
        }

        var lowerCase = !name.Any(char.IsUpper);
        foreach (var suffix in LogMeasurementSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) || name.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase)
                || (lowerCase && suffix != "Ms" && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

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
