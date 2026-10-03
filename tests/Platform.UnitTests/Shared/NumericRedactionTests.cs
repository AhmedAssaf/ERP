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

    private static object? Value(LogEventPropertyValue value) => value.ShouldBeOfType<ScalarValue>().Value;

    private static LogEventPropertyValue Member(StructureValue structure, string name) =>
        structure.Properties.Single(p => p.Name == name).Value;

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
