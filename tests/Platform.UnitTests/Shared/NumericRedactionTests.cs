using System.Diagnostics;
using System.Numerics;
using Platform.Shared.Telemetry;
using Serilog.Core;
using Serilog.Events;

namespace Platform.UnitTests.Shared;

/// <summary>
/// User ruling 2026-10-03 (spec 7.1): a number-typed log property or span tag of ten or more digits is masked like digits in
/// text; small numbers and an explicit allow-list of measurements stay numbers.
/// </summary>
public sealed class NumericRedactionTests
{
    private const long Cr = 1010123456;
    private const long Iqama = 2123456789;

    private static readonly long[] SmallArray = [1, 2, 3];
    private static readonly double[] Doubles = [2.5e9, 1.5];
    private static readonly string[] MaskedFive = ["[digits]", "5"];
    private static readonly string[] MaskedOnly = ["[digits]"];
    private static readonly string[] MaskedHalf = ["[digits]", "1.5"];
    private static readonly long[] TenGbArray = [10_737_418_240];

    [Fact]
    public void A_long_number_property_is_masked_and_a_small_one_stays()
    {
        var sink = new ListSink();
        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]))
        {
            logger.Information(
                "Numbers {Cr} {Iqama} {Neg} {Big} {Dec} {Dbl} {Small} {Edge} {Frac} {Count} {Id}",
                Cr, (ulong)Iqama, -Iqama, new BigInteger(Cr) * 100, 1010123456.5m, 2123456789.0, 12345L, 999_999_999L, 999_999_999.99m, 42, 7);
        }

        var p = sink.Events.ShouldHaveSingleItem().Properties;
        foreach (var name in new[] { "Cr", "Iqama", "Neg", "Big", "Dec", "Dbl" })
        {
            Value(p[name]).ShouldBe("[digits]", name);
        }

        Value(p["Small"]).ShouldBe(12345L);
        Value(p["Edge"]).ShouldBe(999_999_999L);
        Value(p["Frac"]).ShouldBe(999_999_999.99m);
        Value(p["Count"]).ShouldBe(42);
    }

    [Fact]
    public void A_long_number_inside_a_structure_sequence_or_dictionary_is_masked()
    {
        var sink = new ListSink();
        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]))
        {
            logger.Information(
                "Nested {@Vendor} {@Many} {@Map}",
                new { Cr, Small = 5, Inner = new { Iqama } },
                new[] { Cr, 7L, Iqama },
                new Dictionary<string, long> { ["cr"] = Cr, ["n"] = 3 });
        }

        var p = sink.Events.ShouldHaveSingleItem().Properties;
        var vendor = p["Vendor"].ShouldBeOfType<StructureValue>();
        Value(Member(vendor, "Cr")).ShouldBe("[digits]");
        Value(Member(vendor, "Small")).ShouldBe(5);
        Value(Member(Member(vendor, "Inner").ShouldBeOfType<StructureValue>(), "Iqama")).ShouldBe("[digits]");
        p["Many"].ShouldBeOfType<SequenceValue>().Elements.Select(Value).ShouldBe(["[digits]", 7L, "[digits]"]);
        var map = p["Map"].ShouldBeOfType<DictionaryValue>().Elements.ToDictionary(e => (string)e.Key.Value!, e => Value(e.Value));
        map["cr"].ShouldBe("[digits]");
        map["n"].ShouldBe(3L);
    }

    [Fact]
    public void An_allow_listed_measurement_keeps_its_number_even_above_ten_digits()
    {
        var sink = new ListSink();
        const long TenGb = 10_737_418_240;
        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]))
        {
            logger.Information("Done {SizeBytes} {ElapsedMilliseconds} {@Stats} {Other}", TenGb, 3_000_000_000d, new { ContentLength = TenGb }, TenGb);
        }

        var p = sink.Events.ShouldHaveSingleItem().Properties;
        Value(p["SizeBytes"]).ShouldBe(TenGb);
        Value(p["ElapsedMilliseconds"]).ShouldBe(3_000_000_000d);
        Value(Member(p["Stats"].ShouldBeOfType<StructureValue>(), "ContentLength")).ShouldBe(TenGb);
        Value(p["Other"]).ShouldBe("[digits]", "a name that is no listed measurement is masked");
    }

    [Fact]
    public void A_long_number_span_tag_is_masked_and_small_numbers_and_measurements_stay()
    {
        using var span = new Activity("GET") { ActivityTraceFlags = ActivityTraceFlags.Recorded }.Start();
        span.SetTag("vendor.cr", Cr);
        span.SetTag("vendor.iqama", Iqama);
        span.SetTag("vendor.amount", 2123456789.5d);
        span.SetTag("vendor.ids", SmallArray);
        span.SetTag("http.response.status_code", 200);
        span.SetTag("http.response.body.size", 10_737_418_240);
        span.SetTag("db.response.returned_rows", 3_000_000_000);
        span.SetTag("http.server.request.duration", 12_345_678_901d);
        span.SetTag("db.small", 123456789L);
        span.Stop();

        new RedactingSpanProcessor().OnEnd(span);

        span.GetTagItem("vendor.cr").ShouldBe("[digits]");
        span.GetTagItem("vendor.iqama").ShouldBe("[digits]");
        span.GetTagItem("vendor.amount").ShouldBe("[digits]");
        span.GetTagItem("vendor.ids").ShouldBe(SmallArray);
        span.GetTagItem("http.response.status_code").ShouldBe(200);
        span.GetTagItem("http.response.body.size").ShouldBe(10_737_418_240);
        span.GetTagItem("db.response.returned_rows").ShouldBe(3_000_000_000);
        span.GetTagItem("http.server.request.duration").ShouldBe(12_345_678_901d);
        span.GetTagItem("db.small").ShouldBe(123456789L);
    }

    [Fact]
    public void Every_numeric_type_negative_values_and_numeric_dictionary_keys_are_masked_by_the_same_threshold()
    {
        var sink = new ListSink();
        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]))
        {
            logger.Information(
                "Types {I128} {U128} {Flt} {Int} {UInt} {NegLong} {SmallNeg} {SmallI128} {SmallFlt} {@ByNumber}",
                (Int128)Iqama, (UInt128)Cr, 2.5e9f, int.MaxValue, 4_000_000_000u, -Cr, -999_999_999L, (Int128)5, 1.5f,
                new Dictionary<long, string> { [Cr] = "a", [7] = "b" });
        }

        var p = sink.Events.ShouldHaveSingleItem().Properties;
        foreach (var name in new[] { "I128", "U128", "Flt", "Int", "UInt", "NegLong" })
        {
            Value(p[name]).ShouldBe("[digits]", name);
        }

        Value(p["SmallNeg"]).ShouldBe(-999_999_999L);
        Value(p["SmallI128"]).ShouldBe("5", "Serilog renders an Int128 as text");
        Value(p["SmallFlt"]).ShouldBe(1.5f);
        p["ByNumber"].ShouldBeOfType<DictionaryValue>().Elements.Keys.Select(k => k.Value).ShouldBe(["[digits]", (object)7L], ignoreOrder: true);
    }

    [Fact]
    public void The_threshold_applies_to_every_numeric_type_directly()
    {
        foreach (var big in new object[] { (Int128)Iqama, (UInt128)Cr, -(Int128)Cr, new BigInteger(-Cr), 2.5e9f, 2.5e9, 2.5e9m, uint.MaxValue, long.MinValue, ulong.MaxValue })
        {
            NumericRedaction.IsLongNumber(big).ShouldBeTrue(big.GetType().Name);
        }

        foreach (var small in new object[] { (Int128)5, (UInt128)5, new BigInteger(5), 1.5f, 999_999_999.99m, 999_999_999.0, (sbyte)-5, ushort.MaxValue, "1010123456", double.NaN, double.PositiveInfinity })
        {
            NumericRedaction.IsLongNumber(small).ShouldBeFalse(small.GetType().Name);
        }
    }

    [Fact]
    public void The_log_allow_list_ignores_case_matches_endings_and_covers_dictionary_values()
    {
        var sink = new ListSink();
        const long Big = 10_737_418_240;
        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]))
        {
            logger.Information(
                "Allowed {sizebytes} {TotalBytes} {RowsAffected} {total_bytes} {QueueCount} {@Map} {Claims}",
                Big, Big, Big, Big, Big, new Dictionary<string, long> { ["ELAPSEDMS"] = Big, ["Phone"] = Cr }, Cr);
        }

        var p = sink.Events.ShouldHaveSingleItem().Properties;
        foreach (var name in new[] { "sizebytes", "TotalBytes", "RowsAffected", "total_bytes", "QueueCount" })
        {
            Value(p[name]).ShouldBe(Big, name);
        }

        var map = p["Map"].ShouldBeOfType<DictionaryValue>().Elements.ToDictionary(e => (string)e.Key.Value!, e => Value(e.Value));
        map["ELAPSEDMS"].ShouldBe(Big);
        map["Phone"].ShouldBe("[digits]");
        Value(p["Claims"]).ShouldBe("[digits]");
    }

    [Fact]
    public void A_numeric_array_span_tag_is_masked_per_element_unless_a_measurement()
    {
        using var span = new Activity("GET") { ActivityTraceFlags = ActivityTraceFlags.Recorded }.Start();
        span.SetTag("vendor.crs", new[] { Cr, 5L });
        span.SetTag("vendor.ints", new[] { int.MaxValue });
        span.SetTag("vendor.doubles", Doubles);
        span.SetTag("vendor.small", SmallArray);
        span.SetTag("db.sizes.bytes", TenGbArray);
        span.Stop();

        new RedactingSpanProcessor().OnEnd(span);

        span.GetTagItem("vendor.crs").ShouldBe(MaskedFive);
        span.GetTagItem("vendor.ints").ShouldBe(MaskedOnly);
        span.GetTagItem("vendor.doubles").ShouldBe(MaskedHalf);
        span.GetTagItem("vendor.small").ShouldBe(SmallArray);
        span.GetTagItem("db.sizes.bytes").ShouldBe(TenGbArray);
    }

    private static object? Value(LogEventPropertyValue value) => value.ShouldBeOfType<ScalarValue>().Value;

    private static LogEventPropertyValue Member(StructureValue structure, string name) =>
        structure.Properties.Single(p => p.Name == name).Value;

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
