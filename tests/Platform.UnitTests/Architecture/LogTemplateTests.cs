using System.Text.RegularExpressions;

namespace Platform.UnitTests.Architecture;

/// <summary>
/// W-10, plan task 3 (spec O-10 layer 1, section 7.1): at the source, no log template takes a personal or secret value. Every
/// <c>[LoggerMessage(... Message = "...")]</c> template and every <c>Log*(</c> or <c>BeginScope(</c> call with a literal
/// template under <c>src/</c> is read, and a placeholder naming one of the refused words fails the test with its file and
/// line. A placeholder is compared word by word (<c>{UserEmail}</c> holds the word Email, <c>{CrNumber}</c> the words Cr
/// and CrNumber; <c>{Created}</c> holds no refused word), ignoring case.
/// </summary>
public sealed partial class LogTemplateTests
{
    private static readonly string[] Refused =
    [
        "Email", "Name", "DisplayName", "Phone", "Cr", "CrNumber", "NationalId", "Iqama", "Iban", "FileName", "Password",
        "Secret", "Token", "ConnectionString", "Price", "Amount", "Total", "Offer", "Envelope",
    ];

    [Fact]
    public void No_log_template_names_a_personal_or_secret_value()
    {
        var templates = Templates().ToList();
        templates.Count.ShouldBeGreaterThan(0, "the scan found the code base's log templates");

        var offenders = templates
            .SelectMany(t => Placeholders(t.Template).Where(IsRefused).Select(p => $"{t.Location}: {{{p}}} in \"{t.Template}\""))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Every_logger_message_attribute_has_a_literal_template_the_scan_can_read()
    {
        var attributes = SourceFiles().Sum(f => Regex.Count(f.Text, @"\[LoggerMessage\b"));

        Templates().Count(t => t.Kind == "LoggerMessage").ShouldBe(attributes);
    }

    [Theory]
    [InlineData("{Email}")]
    [InlineData("{email}")]
    [InlineData("{UserEmail}")]
    [InlineData("{@ContactName}")]
    [InlineData("{CrNumber}")]
    [InlineData("{CR}")]
    [InlineData("{FileName,10}")]
    [InlineData("{NationalId:l}")]
    [InlineData("{TotalAmount}")]
    [InlineData("{ConnectionString}")]
    [InlineData("{AccessToken}")]
    [InlineData("{offer_envelope}")]
    public void The_rule_refuses_a_placeholder_holding_a_refused_word(string template)
    {
        Placeholders(template).ShouldHaveSingleItem().ShouldSatisfyAllConditions(p => IsRefused(p).ShouldBeTrue(p));
    }

    [Theory]
    [InlineData("{UserId}")]
    [InlineData("{CompanyId}")]
    [InlineData("{DocumentId}")]
    [InlineData("{ErrorType}")]
    [InlineData("{Count}")]
    [InlineData("{Created}")]
    [InlineData("{Scope}")]
    [InlineData("{Description}")]
    public void The_rule_keeps_a_placeholder_without_a_refused_word(string template)
    {
        Placeholders(template).ShouldHaveSingleItem().ShouldSatisfyAllConditions(p => IsRefused(p).ShouldBeFalse(p));
    }

    [Fact]
    public void Escaped_braces_are_not_placeholders()
    {
        Placeholders("Literal {{Email}} and {Count}").ShouldBe(["Count"]);
    }

    private static bool IsRefused(string placeholder)
    {
        var words = Words(placeholder);
        return Refused.Select(Words).Any(term => Enumerable.Range(0, Math.Max(0, words.Count - term.Count + 1))
            .Any(start => words.Skip(start).Take(term.Count).SequenceEqual(term, StringComparer.OrdinalIgnoreCase)));
    }

    private static List<string> Words(string name) => [.. Word().Matches(name).Select(m => m.Value)];

    private static IEnumerable<string> Placeholders(string template) =>
        Placeholder().Matches(template).Where(m => m.Groups["name"].Success).Select(m => m.Groups["name"].Value);

    private static IEnumerable<(string Kind, string Location, string Template)> Templates()
    {
        foreach (var file in SourceFiles())
        {
            foreach (Match attribute in LoggerMessageAttribute().Matches(file.Text))
            {
                if (MessageArgument().Match(attribute.Value) is { Success: true } message)
                {
                    yield return ("LoggerMessage", Location(file, attribute.Index), Literals(message.Groups["literals"].Value));
                }
            }

            foreach (Match call in LogCall().Matches(file.Text))
            {
                yield return ("Call", Location(file, call.Index), Literals(call.Groups["literals"].Value));
            }
        }
    }

    private static string Literals(string concatenation) =>
        string.Concat(StringLiteral().Matches(concatenation).Select(m => Regex.Unescape(m.Groups["body"].Value)));

    private static string Location((string Path, string Text) file, int index) =>
        $"{Path.GetRelativePath(TestRepo.Root, file.Path)}:{file.Text.AsSpan(0, index).Count('\n') + 1}";

    private static List<(string Path, string Text)> SourceFiles() =>
        [.. Directory.EnumerateFiles(TestRepo.Src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (f, File.ReadAllText(f)))];

    /// <summary>A whole <c>[LoggerMessage(...)]</c> attribute, across lines.</summary>
    [GeneratedRegex(@"\[LoggerMessage\s*\((?:[^()""]|""(?:[^""\\]|\\.)*"")*\)\s*\]", RegexOptions.Singleline)]
    private static partial Regex LoggerMessageAttribute();

    /// <summary>The attribute's <c>Message = "..." + "..."</c>: one or more concatenated literals.</summary>
    [GeneratedRegex(@"\bMessage\s*=\s*(?<literals>""(?:[^""\\]|\\.)*""(?:\s*\+\s*""(?:[^""\\]|\\.)*"")*)", RegexOptions.Singleline)]
    private static partial Regex MessageArgument();

    /// <summary>
    /// A <c>Log</c>, <c>LogTrace</c> to <c>LogCritical</c> or <c>BeginScope</c> call whose first string argument is a literal
    /// (after an optional level, event id or exception argument).
    /// </summary>
    [GeneratedRegex(@"\.(?:Log(?:Trace|Debug|Information|Warning|Error|Critical)?|BeginScope)\s*\((?:[^;""()]|\([^;""()]*\))*?(?<literals>""(?:[^""\\]|\\.)*""(?:\s*\+\s*""(?:[^""\\]|\\.)*"")*)", RegexOptions.Singleline)]
    private static partial Regex LogCall();

    [GeneratedRegex(@"""(?<body>(?:[^""\\]|\\.)*)""")]
    private static partial Regex StringLiteral();

    /// <summary>A Serilog or Microsoft.Extensions.Logging placeholder; <c>{{</c> and <c>}}</c> are escaped braces.</summary>
    [GeneratedRegex(@"\{\{|\}\}|\{[@$]?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    /// <summary>The words of a placeholder name: PascalCase parts, an upper-case run, digits; underscores separate.</summary>
    [GeneratedRegex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+")]
    private static partial Regex Word();
}
