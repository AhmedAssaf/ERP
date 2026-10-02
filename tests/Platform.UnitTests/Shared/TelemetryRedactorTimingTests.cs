using System.Diagnostics;
using System.Text;
using Platform.Shared.Telemetry;

namespace Platform.UnitTests.Shared;

/// <summary>
/// W-10, plan task 3, fix round 1: the redactor runs on every log property and span tag, client-controlled ones included
/// (<c>user_agent.original</c>, <c>url.path</c>), so it must take linear time. Each case is an input crafted against one
/// pattern (a long run that a backtracking search would rescan from every start); 32 and 64 KB must each take well under
/// 50 ms.
/// </summary>
public sealed class TelemetryRedactorTimingTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(50);

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in Inputs.Keys)
        {
            data.Add(name, 32 * 1024);
            data.Add(name, 64 * 1024);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void An_adversarial_value_is_redacted_in_linear_time(string pattern, int length)
    {
        var value = Inputs[pattern](length);
        TelemetryRedactor.Redact(Inputs[pattern](256));

        var fastest = TimeSpan.MaxValue;
        for (var attempt = 0; attempt < 3 && fastest >= Budget; attempt++)
        {
            var watch = Stopwatch.StartNew();
            TelemetryRedactor.Redact(value);
            watch.Stop();
            fastest = watch.Elapsed < fastest ? watch.Elapsed : fastest;
        }

        fastest.ShouldBeLessThan(Budget, $"{pattern} at {length} characters took {fastest.TotalMilliseconds:F1} ms");
    }

    private static readonly Dictionary<string, Func<int, string>> Inputs = new(StringComparer.Ordinal)
    {
        // The reviewer's input: dotted words with no key until the very end.
        ["secret pair, dotted run"] = n => Repeat("a.", n - 7) + " pwd x=",
        ["secret pair, many keys"] = n => Repeat("passwordpwd", n - 1) + "=",
        ["email, local part without domain"] = n => Repeat("a", n - 1) + "@",
        ["email, many at signs"] = n => Repeat("a@a.", n),
        ["email, encoded at signs"] = n => Repeat("a%40", n),
        ["jwt, dashed headers"] = n => Repeat("eyJ-", n),
        ["jwt, one dot only"] = n => "eyJ" + Repeat("a", n - 4) + ".",
        ["bearer, repeated"] = n => Repeat("bearer ", n),
        ["bearer, long white space"] = n => "Bearer" + Repeat(" ", n - 6),
        ["digits, runs glued to letters"] = n => Repeat("1234567890a", n),
        ["digits, dashed runs"] = n => Repeat("1234567890-", n),
        ["digits, guid-like dashed run"] = n => Repeat("aaaaaaaa-aaaa-aaaa-aaaa-123456789012-", n),
        ["every pattern at once"] = n => Repeat("a.", n - 20) + "@ eyJ bearer pwd x=",
        // W-10 task 4 ruling: Basic credentials and Authorization header lines.
        ["basic, repeated"] = n => Repeat("basic ", n),
        ["basic, long white space"] = n => "Basic" + Repeat(" ", n - 6) + "x",
        ["basic, one long credential"] = n => "Basic " + Repeat("QUFB", n - 6),
        ["basic, many credentials"] = n => Repeat("Basic dXNlcjpwYXNz ", n),
        ["authorization, repeated"] = n => Repeat("authorization: ", n),
        ["authorization, long value"] = n => "Authorization: " + Repeat("x", n - 15),
        ["authorization, long white space"] = n => "Authorization:" + Repeat(" ", n - 15) + "x",
    };

    private static string Repeat(string unit, int length)
    {
        var builder = new StringBuilder(length + unit.Length);
        while (builder.Length < length)
        {
            builder.Append(unit);
        }

        return builder.ToString(0, Math.Max(0, length));
    }
}
