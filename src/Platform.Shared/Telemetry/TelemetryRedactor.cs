using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Platform.Shared.Telemetry;

/// <summary>
/// The one masking step of W-10's in-process redaction (plan task 3, final fix wave; spec O-10, section 7.1), used by the log
/// pipeline (<see cref="RedactingEnricher"/>, <see cref="RedactedEventSink"/>) and by the span processor
/// (<see cref="RedactingSpanProcessor"/>). It catches framework and library text we do not write; our own templates never
/// take such values in the first place (the template test). Each match becomes a fixed marker, so a reader sees that
/// something was removed:
/// <list type="bullet">
/// <item>an email address, also URL-encoded (<c>%40</c>), becomes <c>[email]</c>: the whole local part, every character
/// RFC 5322 allows unquoted (all that <see cref="Text.EmailAddresses"/> accepts) and letters of any script, and a domain of
/// dotted labels of letters, marks and digits of any script (an internationalised name such as <c>شركة.السعودية</c>);</item>
/// <item>a JWT (<c>eyJ...</c> with two dots) becomes <c>[token]</c>, and the credential after <c>Bearer</c> as well;</item>
/// <item>the credential after <c>Basic</c> (base64 that decodes to <c>user:password</c>, so "basic plan" is kept) becomes
/// <c>[token]</c>, and so does the value of an <c>Authorization:</c> header line whatever its scheme, up to the end of the
/// line (W-10 task 4 ruling); a value already reduced to <c>Bearer [token]</c> or <c>Basic [token]</c> keeps its scheme;</item>
/// <item>the value of a <c>password</c>, <c>pwd</c>, <c>secret</c> or <c>apikey</c> pair (any key ending so, such as
/// <c>client_secret</c>; case-insensitive) becomes <c>[secret]</c>: <c>key=value</c> (up to <c>;</c>, <c>&amp;</c> or white
/// space; written back as <c>key=[secret]</c>), <c>key: value</c> (up to <c>;</c>, <c>&amp;</c>, <c>,</c>, <c>}</c>, a quote
/// or white space) and the JSON forms <c>"key":"value"</c> and <c>"key": "value"</c> (the whole quoted value), the colon
/// forms with their separator kept;</item>
/// <item>a run of ten or more decimal digits of any script becomes <c>[digits]</c>: CR, national id, iqama, phone, VAT and
/// IBAN numbers all have ten or more. Digit groups joined as people write phones and IBANs count as one run
/// (<c>055 123 4567</c>, <c>055-123 4567</c>, <c>(011) 465 1234</c>, <c>+966 5 5123 4567</c>, <c>055.123.4567</c>,
/// <c>SA03-8000-0000-6080-1016-7519</c>), while a date and time (<c>2026-10-02 12:34:56</c>), an IP address, a version, a
/// list of single digits and a reference (<c>RFP-2026-000045</c>, <c>W-10-2026-09-30</c>, <c>2026-10-02-15</c>) are kept;
/// the rules are on <see cref="MaskDigitRuns"/>. A run inside a hexadecimal identifier (a span, trace or GUID "N" id:
/// exactly 16 or 32 lower-case hexadecimal characters with at least one letter) or inside a GUID is kept.</item>
/// </list>
/// A value that is not pure ASCII is matched on a normalised copy: Unicode format characters (zero-width joiners and
/// spaces, direction marks and isolates, the soft hyphen) removed, then NFKC (full-width <c>＠</c> and digits become ASCII),
/// so none of them can split or disguise a value. A value with nothing to mask comes back as the same instance, without
/// allocating; a masked one comes back as the normalised copy with its markers. <see cref="IsSecretKey"/> names the keys
/// whose whole value is a secret, whatever it holds (the log pipeline replaces the value, the span processor drops the tag).
/// </summary>
public static partial class TelemetryRedactor
{
    public const string EmailMarker = "[email]";
    public const string DigitsMarker = "[digits]";
    public const string TokenMarker = "[token]";
    public const string SecretMarker = "[secret]";

    private const int DigitRunLength = 10;
    private const int SpanIdLength = 16;
    private const int TraceIdLength = 32;

    /// <summary>
    /// The options of every free-length pattern: the non-backtracking engine matches in time linear in the input, whatever
    /// the input (values here include client-controlled text such as a user agent or a request path).
    /// </summary>
    private const RegexOptions Linear = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    /// <summary>Masks every email, token, secret pair and long digit run in <paramref name="value"/>.</summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Matched on the normalised copy; the copy leaves only when something in it was masked.
        var text = Ascii.IsValid(value) ? value : Normalised(value);
        var masked = Mask(text);
        return ReferenceEquals(masked, text) ? value : masked;
    }

    /// <summary>
    /// True for a key whose whole value is a secret (spec O-10, O-11; W-10 final fix wave): ignoring case and the separators
    /// <c>-</c>, <c>_</c> and <c>.</c>, a name ending in authorization, cookie, password, passwd, pwd, secret, token, apikey
    /// or connectionstring (<c>Proxy-Authorization</c>, <c>Set-Cookie</c>, <c>client_secret</c>, <c>access_token</c>,
    /// <c>X-Api-Key</c>), and Npgsql's <c>db.client.connection.pool.name</c> and <c>db.npgsql.data_source</c>, which hold a
    /// connection string when a data source has no name. Used for log property names, dictionary keys, structure members and
    /// span tag keys.
    /// </summary>
    public static bool IsSecretKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        Span<char> buffer = key.Length <= 256 ? stackalloc char[key.Length] : new char[key.Length];
        var length = 0;
        foreach (var c in key)
        {
            if (c is not ('-' or '_' or '.'))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        var compact = buffer[..length];
        foreach (var suffix in SecretKeySuffixes)
        {
            if (compact.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var name in SecretKeyNames)
        {
            if (compact.SequenceEqual(name))
            {
                return true;
            }
        }

        return false;
    }

    private static string Mask(string value)
    {
        // One pattern per kind, each run only when the text holds what every match of it needs; every free-length pattern
        // is linear (Linear). Regex.Replace returns the very instance it was given when nothing matched. Secret pairs go
        // first, so a pair whose value is an email ends as [secret] and its key is not taken into an email's local part.
        var masked = value;
        if ((masked.Contains('=') || masked.Contains(':')) && HoldsSecretKey(masked))
        {
            masked = SecretPair().Replace(masked, SecretValue);
        }

        if (masked.Contains('@') || masked.Contains("%40", StringComparison.Ordinal))
        {
            masked = Email().Replace(masked, EmailMarker);
        }

        if (masked.Contains("eyJ", StringComparison.Ordinal))
        {
            masked = Jwt().Replace(masked, TokenMarker);
        }

        if (masked.Contains("bearer", StringComparison.OrdinalIgnoreCase))
        {
            masked = Bearer().Replace(masked, "${bearer}" + TokenMarker);
        }

        if (masked.Contains("basic", StringComparison.OrdinalIgnoreCase))
        {
            masked = MaskGroup(masked, Basic(), "credential", IsBasicCredential);
        }

        if (masked.Contains("authorization", StringComparison.OrdinalIgnoreCase))
        {
            masked = MaskGroup(masked, AuthorizationHeader(), "value", IsUnmaskedHeaderValue);
        }

        return MaskDigitRuns(masked);
    }

    /// <summary>
    /// The copy the patterns match on: format characters (category Cf) removed, a lone surrogate replaced with U+FFFD (NFKC
    /// refuses one), then NFKC. The same instance when nothing changes.
    /// </summary>
    private static string Normalised(string value)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var pair = char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]);
            var lone = !pair && char.IsSurrogate(c);
            var format = !lone && CharUnicodeInfo.GetUnicodeCategory(value, i) == UnicodeCategory.Format;
            if ((lone || format) && builder is null)
            {
                builder = new StringBuilder(value.Length).Append(value, 0, i);
            }

            if (lone)
            {
                builder!.Append('�');
            }
            else if (!format && builder is not null)
            {
                builder.Append(value, i, pair ? 2 : 1);
            }

            if (pair)
            {
                i++;
            }
        }

        var stripped = builder?.ToString() ?? value;
        return stripped.IsNormalized(NormalizationForm.FormKC) ? stripped : stripped.Normalize(NormalizationForm.FormKC);
    }

    /// <summary>The key and separator of a secret pair, then the marker; the <c>=</c> form is written back as <c>key=</c>.</summary>
    private static readonly MatchEvaluator SecretValue = static match => match.Groups["sep"] is { Success: true } separator
        ? string.Concat(match.Groups["key"].ValueSpan, separator.ValueSpan, SecretMarker)
        : string.Concat(match.Groups["key"].ValueSpan, "=", SecretMarker);

    /// <summary>
    /// Replaces the named group of every match <paramref name="mask"/> accepts with <see cref="TokenMarker"/>. A value where no
    /// match is accepted comes back as the same instance.
    /// </summary>
    private static string MaskGroup(string value, Regex pattern, string group, Func<Group, bool> mask)
    {
        StringBuilder? builder = null;
        var copied = 0;
        foreach (Match match in pattern.Matches(value))
        {
            var target = match.Groups[group];
            if (!mask(target))
            {
                continue;
            }

            builder ??= new StringBuilder(value.Length);
            builder.Append(value, copied, target.Index - copied).Append(TokenMarker);
            copied = target.Index + target.Length;
        }

        return builder is null ? value : builder.Append(value, copied, value.Length - copied).ToString();
    }

    /// <summary>
    /// A Basic credential is base64 of <c>user:password</c>: only a run that decodes to bytes holding a colon is masked, so an
    /// ordinary word after "basic" (also valid base64 characters) is kept.
    /// </summary>
    private static bool IsBasicCredential(Group credential)
    {
        var chars = credential.ValueSpan;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((chars.Length * 3 / 4) + 3);
        try
        {
            return Convert.TryFromBase64Chars(chars, buffer, out var written) && buffer.AsSpan(0, written).Contains((byte)':');
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Any header value, unless an earlier step already left only a scheme and the marker (<c>Bearer [token]</c>).</summary>
    private static bool IsUnmaskedHeaderValue(Group value)
    {
        var text = value.ValueSpan;
        if (!text.EndsWith(TokenMarker, StringComparison.Ordinal))
        {
            return true;
        }

        var beforeMarker = text[..^TokenMarker.Length];
        var scheme = beforeMarker.TrimEnd();
        return scheme.Length == 0 || scheme.Length == beforeMarker.Length || scheme.ContainsAnyExcept(SchemeCharacters);
    }

    private static bool HoldsSecretKey(string value)
    {
        foreach (var key in SecretKeys)
        {
            if (value.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Masks the runs of ten or more digits (W-10 follow-ups 2026-10-02: phones as people group them, references and dates
    /// kept). One pass collects the digit groups (maximal runs of digits) token by token (a token is a maximal run of ASCII
    /// letters and digits); a group in a hexadecimal identifier or a GUID is kept and breaks any chain. A second pass, left to
    /// right, takes at each group the first of these that applies:
    /// <list type="number">
    /// <item>a date, three groups joined by the same <c>-</c> or <c>.</c>, <c>yyyy-mm-dd</c> or <c>dd-mm-yyyy</c> with a year
    /// from 1900 to 2099: kept, and it breaks any chain (<c>2026-10-02 12:34</c>, <c>2026-10-02-15</c>);</item>
    /// <item>a reference, a group right after a word of ASCII letters and a hyphen that is a year from 2000 to 2049, or a
    /// number of one to three digits (no leading zero) followed by <c>-</c> and such a year, with the groups after it joined
    /// by <c>-</c>, each under ten digits, at most 16 digits and five groups in all: kept (<c>RFP-2026-000045</c>,
    /// <c>W-10-2026-09-30</c>). A longer one is an ordinary chain (<c>acct-2026-0000-6080-1016-7519</c> is masked);</item>
    /// <item>a dotted run, groups joined by single dots: masked whole when it has three or more groups of two or more
    /// digits, ten digits or more in all, and is not shaped like an IPv4 address (four groups of up to three digits); kept
    /// otherwise (<c>055.123.4567</c> masked; <c>192.168.100.200</c>, <c>10.0.26100.4061</c>, <c>12345678.90</c> kept);</item>
    /// <item>otherwise a chain: groups joined by a phone separator (an optional <c>)</c>, up to two spaces or tabs, an
    /// optional <c>-</c>, up to two spaces or tabs, an optional <c>(</c>; spaces and hyphens may mix: <c>055-123 4567</c>,
    /// <c>(011) 465 1234</c>), where a group of one digit joins only as a token of its own and never next to another
    /// single digit (<c>+966 5 5123 4567</c> and <c>0 55 123 4567</c> join; <c>1 2 3 4 5 6 7 8 9 10</c> does not). The
    /// chain stops before a date and before a group that starts a dotted run; with ten or more digits it is masked.</item>
    /// </list>
    /// A single group of ten or more digits is always masked, wherever it stands. Every step looks at a bounded number of
    /// characters per group or consumes the groups it reads, so the time stays linear in the value.
    /// </summary>
    private static string MaskDigitRuns(string value)
    {
        if (!HoldsDigitRunLength(value))
        {
            return value;
        }

        return new DigitChains(value, DigitGroups(value)).Mask();
    }

    /// <summary>True when the value holds at least ten digits in all, the least any masked run needs.</summary>
    private static bool HoldsDigitRunLength(string value)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (IsDigit(c) && ++count >= DigitRunLength)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A maximal run of digits, <c>value[Start..End]</c>. <see cref="Kept"/>: in a hexadecimal identifier or a GUID.
    /// <see cref="Whole"/>: the whole token. <see cref="AfterWord"/>: the token starts right after a word of ASCII letters and
    /// a hyphen (<c>RFP-</c>).
    /// </summary>
    private readonly record struct DigitGroup(int Start, int End, bool Kept, bool Whole, bool AfterWord)
    {
        public int Length => End - Start;
    }

    /// <summary>Every digit group of the value, in order (<see cref="MaskDigitRuns"/>).</summary>
    private static List<DigitGroup> DigitGroups(string value)
    {
        var groups = new List<DigitGroup>();
        var guids = new GuidRanges(value);
        var previousEnd = -1;
        var previousIsWord = false;
        var i = 0;
        while (i < value.Length)
        {
            if (!IsTokenChar(value[i]))
            {
                i++;
                continue;
            }

            var tokenStart = i;
            var isWord = true;
            while (i < value.Length && IsTokenChar(value[i]))
            {
                isWord &= char.IsAsciiLetter(value[i]);
                i++;
            }

            var hexIdentifier = IsHexIdentifier(value.AsSpan(tokenStart, i - tokenStart));
            var afterWord = previousIsWord && previousEnd == tokenStart - 1 && value[previousEnd] == '-';
            for (var j = tokenStart; j < i;)
            {
                if (!IsDigit(value[j]))
                {
                    j++;
                    continue;
                }

                var start = j;
                while (j < i && IsDigit(value[j]))
                {
                    j++;
                }

                var kept = hexIdentifier || guids.Contains(start, j);
                groups.Add(new DigitGroup(start, j, kept, start == tokenStart && j == i, afterWord && start == tokenStart));
            }

            previousEnd = i;
            previousIsWord = isWord;
        }

        return groups;
    }

    /// <summary>What separates two neighbouring digit groups (<see cref="DigitChains.Between"/>).</summary>
    private enum Separator
    {
        None,
        Hyphen,
        Dot,
        Phone,
    }

    /// <summary>The masking pass over the digit groups of one value, and the masked copy, if any (<see cref="MaskDigitRuns"/>).</summary>
    private sealed class DigitChains(string value, List<DigitGroup> groups)
    {
        private const int MaxReferenceDigits = 16;
        private const int MaxReferenceGroups = 5;

        private StringBuilder? _builder;
        private int _copied;

        public string Mask()
        {
            var i = 0;
            while (i < groups.Count)
            {
                if (groups[i].Kept)
                {
                    i++;
                }
                else if (IsDate(i))
                {
                    i += 3;
                }
                else if (ReferenceEnd(i) is var reference and > 0)
                {
                    i = reference;
                }
                else if (Between(i, i + 1) == Separator.Dot)
                {
                    i = DottedRun(i);
                }
                else
                {
                    i = Chain(i);
                }
            }

            return _builder is null ? value : _builder.Append(value, _copied, value.Length - _copied).ToString();
        }

        /// <summary>A chain from group <paramref name="first"/>, masked at ten digits or more; the index after its last group.</summary>
        private int Chain(int first)
        {
            var last = first;
            var digits = groups[first].Length;
            while (CanExtend(last, last + 1))
            {
                last++;
                digits += groups[last].Length;
            }

            if (digits >= DigitRunLength)
            {
                Replace(groups[first].Start, groups[last].End);
            }

            return last + 1;
        }

        private bool CanExtend(int previous, int next)
        {
            if (next >= groups.Count || groups[next].Kept || Between(previous, next) is not (Separator.Hyphen or Separator.Phone))
            {
                return false;
            }

            var (before, after) = (groups[previous], groups[next]);
            return (before.Length > 1 || before.Whole) && (after.Length > 1 || after.Whole) && (before.Length > 1 || after.Length > 1)
                && !IsDate(next) && Between(next, next + 1) != Separator.Dot;
        }

        /// <summary>
        /// The dotted run from group <paramref name="first"/>: masked whole when shaped like a grouped number, otherwise only
        /// its groups of ten digits or more; the index after its last group.
        /// </summary>
        private int DottedRun(int first)
        {
            var end = first + 1;
            while (end < groups.Count && !groups[end].Kept && Between(end - 1, end) == Separator.Dot)
            {
                end++;
            }

            var count = end - first;
            int digits = 0, shortest = int.MaxValue, longest = 0;
            for (var k = first; k < end; k++)
            {
                digits += groups[k].Length;
                shortest = Math.Min(shortest, groups[k].Length);
                longest = Math.Max(longest, groups[k].Length);
            }

            var ipv4 = count == 4 && longest <= 3;
            if (count >= 3 && shortest >= 2 && digits >= DigitRunLength && !ipv4)
            {
                Replace(groups[first].Start, groups[end - 1].End);
            }
            else
            {
                for (var k = first; k < end; k++)
                {
                    if (groups[k].Length >= DigitRunLength)
                    {
                        Replace(groups[k].Start, groups[k].End);
                    }
                }
            }

            return end;
        }

        /// <summary>The index after a reference starting at group <paramref name="first"/>, or -1 when none starts there.</summary>
        private int ReferenceEnd(int first)
        {
            var group = groups[first];
            if (!group.AfterWord)
            {
                return -1;
            }

            int year;
            if (IsReferenceYear(first))
            {
                year = first;
            }
            else if (group.Length <= 3 && CharUnicodeInfo.GetDecimalDigitValue(value[group.Start]) != 0
                && Between(first, first + 1) == Separator.Hyphen && IsReferenceYear(first + 1))
            {
                year = first + 1;
            }
            else
            {
                return -1;
            }

            var end = year + 1;
            while (end < groups.Count && end - first <= MaxReferenceGroups && !groups[end].Kept && groups[end].Length < DigitRunLength
                && Between(end - 1, end) == Separator.Hyphen)
            {
                end++;
            }

            var digits = 0;
            for (var k = first; k < end; k++)
            {
                digits += groups[k].Length;
            }

            return end - first > MaxReferenceGroups || digits > MaxReferenceDigits ? -1 : end;
        }

        private bool IsReferenceYear(int index) =>
            index < groups.Count && !groups[index].Kept && groups[index].Length == 4 && Number(groups[index]) is >= 2000 and <= 2049;

        /// <summary>True when groups <paramref name="first"/> to <paramref name="first"/> + 2 are a date (<c>yyyy-mm-dd</c> or <c>dd-mm-yyyy</c>).</summary>
        private bool IsDate(int first)
        {
            if (first + 2 >= groups.Count || groups[first + 1].Kept || groups[first + 2].Kept)
            {
                return false;
            }

            var gap = Between(first, first + 1);
            if (gap is not (Separator.Hyphen or Separator.Dot) || Between(first + 1, first + 2) != gap)
            {
                return false;
            }

            var (a, b, c) = (groups[first], groups[first + 1], groups[first + 2]);
            return (a.Length, b.Length, c.Length) switch
            {
                (4, 2, 2) => IsYear(a) && IsMonth(b) && IsDay(c),
                (2, 2, 4) => IsDay(a) && IsMonth(b) && IsYear(c),
                _ => false,
            };
        }

        private bool IsYear(DigitGroup group) => Number(group) is >= 1900 and <= 2099;

        private bool IsMonth(DigitGroup group) => Number(group) is >= 1 and <= 12;

        private bool IsDay(DigitGroup group) => Number(group) is >= 1 and <= 31;

        /// <summary>The value of a group of at most four digits of any script.</summary>
        private int Number(DigitGroup group)
        {
            var number = 0;
            for (var k = group.Start; k < group.End; k++)
            {
                number = (number * 10) + CharUnicodeInfo.GetDecimalDigitValue(value[k]);
            }

            return number;
        }

        /// <summary>
        /// The separator between groups <paramref name="previous"/> and <paramref name="next"/>: one <c>-</c>, one <c>.</c>,
        /// a phone separator (<see cref="MaskDigitRuns"/>), or none of them. At most seven characters are read.
        /// </summary>
        public Separator Between(int previous, int next)
        {
            if (next >= groups.Count)
            {
                return Separator.None;
            }

            var gap = value.AsSpan(groups[previous].End, groups[next].Start - groups[previous].End);
            if (gap.Length == 1 && gap[0] is '-' or '.')
            {
                return gap[0] == '-' ? Separator.Hyphen : Separator.Dot;
            }

            var k = 0;
            if (k < gap.Length && gap[k] == ')')
            {
                k++;
            }

            k = SkipBlanks(gap, k);
            if (k < gap.Length && gap[k] == '-')
            {
                k++;
            }

            k = SkipBlanks(gap, k);
            if (k < gap.Length && gap[k] == '(')
            {
                k++;
            }

            return k == gap.Length && k > 0 ? Separator.Phone : Separator.None;
        }

        private static int SkipBlanks(ReadOnlySpan<char> gap, int k)
        {
            for (var blanks = 0; blanks < 2 && k < gap.Length && gap[k] is ' ' or '\t'; blanks++)
            {
                k++;
            }

            return k;
        }

        private void Replace(int start, int end)
        {
            _builder ??= new StringBuilder(value.Length);
            _builder.Append(value, _copied, start - _copied).Append(DigitsMarker);
            _copied = end;
        }
    }

    /// <summary>The GUIDs of one value, found once by shape on the first question, asked in increasing order.</summary>
    private sealed class GuidRanges(string value)
    {
        private List<Range>? _guids;
        private int _index;

        /// <summary>True when <c>value[start..end]</c> lies in a GUID; calls come in increasing <paramref name="start"/>.</summary>
        public bool Contains(int start, int end)
        {
            _guids ??= Guids(value);
            while (_index < _guids.Count && _guids[_index].End.Value <= start)
            {
                _index++;
            }

            return _index < _guids.Count && _guids[_index].Start.Value <= start && end <= _guids[_index].End.Value;
        }
    }

    /// <summary>A decimal digit of any script (Unicode Nd): Western, Arabic-Indic, Extended Arabic-Indic, Devanagari and so on.</summary>
    private static bool IsDigit(char c) => char.IsDigit(c);

    /// <summary>
    /// A span id, or a trace or GUID "N" id: exactly 16 or 32 lower-case hexadecimal characters, at least one of them a letter,
    /// as .NET and W3C write them. Any other length, or upper case, is not an id: a compact IBAN whose country letters happen to
    /// be hexadecimal (<c>AE07...</c>, <c>DE89...</c>, the 16-character <c>BE68...</c>) has its digits masked (W-10 follow-up,
    /// PDPL). GUIDs with dashes are recognised by their shape, in either case (<see cref="Guids"/>).
    /// </summary>
    private static bool IsHexIdentifier(ReadOnlySpan<char> token) =>
        token.Length is SpanIdLength or TraceIdLength && !token.ContainsAnyExcept(HexCharacters) && token.ContainsAny(HexLetters);

    /// <summary>
    /// Every GUID (8-4-4-4-12 hexadecimal) in the value that no letter or digit touches on either side, in order. A dash may
    /// touch it (<c>doc-&lt;guid&gt;</c>, two GUIDs joined by a dash).
    /// </summary>
    private static List<Range> Guids(string value)
    {
        var guids = new List<Range>();
        if (!value.Contains('-'))
        {
            return guids;
        }

        foreach (var match in GuidShape().EnumerateMatches(value))
        {
            var end = match.Index + match.Length;
            if ((match.Index == 0 || !IsTokenChar(value[match.Index - 1])) && (end == value.Length || !IsTokenChar(value[end])))
            {
                guids.Add(match.Index..end);
            }
        }

        return guids;
    }

    private static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || IsDigit(c);

    private static readonly System.Buffers.SearchValues<char> HexCharacters =
        System.Buffers.SearchValues.Create("0123456789abcdef");

    private static readonly System.Buffers.SearchValues<char> HexLetters =
        System.Buffers.SearchValues.Create("abcdef");

    private static readonly System.Buffers.SearchValues<char> SchemeCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ-");

    /// <summary>The words every key of <see cref="SecretPair"/> holds.</summary>
    private static readonly string[] SecretKeys = ["pass", "pwd", "secret", "apikey", "api_key", "api-key"];

    /// <summary>The endings of a secret key name once lower-cased without separators (<see cref="IsSecretKey"/>).</summary>
    private static readonly string[] SecretKeySuffixes =
        ["authorization", "cookie", "password", "passwd", "pwd", "secret", "token", "apikey", "connectionstring"];

    /// <summary>Whole secret key names once lower-cased without separators: Npgsql's pool and data source names.</summary>
    private static readonly string[] SecretKeyNames = ["dbclientconnectionpoolname", "dbnpgsqldatasource"];

    /// <summary>
    /// An email address. With <c>@</c>: a local part of the RFC 5322 unquoted characters (letters, digits,
    /// <c>! # $ % &amp; ' * + - / = ? ^ _ ` { | } ~</c> and dots) and letters, marks and digits of any script. With its URL
    /// encoding <c>%40</c>: a local part of the characters a query string leaves unencoded (letters, digits,
    /// <c>. _ % + -</c>), so the <c>?</c>, <c>=</c> and <c>&amp;</c> around it are not taken in. Then a domain of two or more
    /// dotted labels of letters, marks, digits and hyphens of any script.
    /// </summary>
    [GeneratedRegex(@"(?:[\p{L}\p{M}\p{N}!#$%&'*+/=?^_`{|}~.\-]+@|[\p{L}\p{M}\p{N}._%+\-]+%40)[\p{L}\p{M}\p{N}\-]+(?:\.[\p{L}\p{M}\p{N}\-]+)+", Linear)]
    private static partial Regex Email();

    /// <summary>A JWT: a base64url header starting <c>eyJ</c>, a payload and a signature, separated by dots.</summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]*\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*", Linear)]
    private static partial Regex Jwt();

    /// <summary>The credential after <c>Bearer</c> (any case); the word and its white space are kept.</summary>
    [GeneratedRegex(@"(?<bearer>\b(?i:bearer)\s+)[A-Za-z0-9\-._~+/]+=*", Linear)]
    private static partial Regex Bearer();

    /// <summary>A candidate Basic credential after <c>Basic</c> (any case); <see cref="IsBasicCredential"/> decides.</summary>
    [GeneratedRegex(@"(?<basic>\b(?i:basic)\s+)(?<credential>[A-Za-z0-9+/]+=*)", Linear)]
    private static partial Regex Basic();

    /// <summary>
    /// An <c>Authorization</c> (or <c>Proxy-Authorization</c>) header line: the name, a colon and spaces or tabs on one line
    /// are kept, the value up to the end of the line is masked (<see cref="IsUnmaskedHeaderValue"/>).
    /// </summary>
    [GeneratedRegex(@"(?<header>\b(?i:authorization)[ \t]*:[ \t]*)(?<value>[^\r\n]+)", Linear)]
    private static partial Regex AuthorizationHeader();

    /// <summary>
    /// A secret key-value pair; the key (the whole run of key characters ending in a secret word) keeps its spelling. Three
    /// forms, tried in this order: a colon and a quoted value (JSON, <c>"key": "value"</c>; the value up to the closing quote
    /// or the end of the line, an escaped quote or backslash inside it included), a colon and a bare value
    /// (<c>key: value</c>), and <c>key = value</c>. The colon forms keep
    /// their separator (group <c>sep</c>); the last is written back as <c>key=</c>. No word boundary and no lazy prefix: the
    /// leftmost match starts at the beginning of the run by itself.
    /// </summary>
    [GeneratedRegex(@"(?<key>[A-Za-z0-9_.\-]*(?i:password|passwd|pwd|secret|api[_\-]?key))(?:(?<sep>""?\s*:\s*"")(?:[^""\\\r\n]|\\.)*|(?<sep>""?\s*:\s*)[^;&\s"",}]*|\s*=\s*[^;&\s]*)", Linear)]
    private static partial Regex SecretPair();

    /// <summary>The GUID shape; fixed length, so each start is tried over at most 36 characters.</summary>
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidShape();
}
