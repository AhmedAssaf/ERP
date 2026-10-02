using System.Text;
using System.Text.RegularExpressions;

namespace Platform.Shared.Telemetry;

/// <summary>
/// The one masking step of W-10's in-process redaction (plan task 3; spec O-10, section 7.1), used by the log pipeline
/// (<see cref="RedactingEnricher"/>, <see cref="RedactedEventSink"/>) and by the span processor
/// (<see cref="RedactingSpanProcessor"/>). It catches framework and library text we do not write; our own templates never
/// take such values in the first place (the template test). Each match becomes a fixed marker, so a reader sees that
/// something was removed:
/// <list type="bullet">
/// <item>an email address, also URL-encoded (<c>%40</c>), becomes <c>[email]</c>;</item>
/// <item>a JWT (<c>eyJ...</c> with two dots) becomes <c>[token]</c>, and the credential after <c>Bearer</c> as well;</item>
/// <item>the credential after <c>Basic</c> (base64 that decodes to <c>user:password</c>, so "basic plan" is kept) becomes
/// <c>[token]</c>, and so does the value of an <c>Authorization:</c> header line whatever its scheme, up to the end of the
/// line (W-10 task 4 ruling); a value already reduced to <c>Bearer [token]</c> or <c>Basic [token]</c> keeps its scheme;</item>
/// <item>the value of a <c>password=</c>, <c>pwd=</c>, <c>secret=</c> or <c>apikey=</c> pair (any key ending so, such as
/// <c>client_secret</c>; case-insensitive; up to <c>;</c>, <c>&amp;</c> or white space) becomes <c>key=[secret]</c>;</item>
/// <item>a run of ten or more digits, Western or Arabic-Indic, becomes <c>[digits]</c>: CR, national id, iqama, phone,
/// VAT and IBAN numbers all have ten or more. A run inside a hexadecimal identifier (a trace or span id: sixteen or more
/// hexadecimal characters with at least one letter) or inside a GUID is kept.</item>
/// </list>
/// A value with nothing to mask comes back as the same instance, without allocating.
/// </summary>
public static partial class TelemetryRedactor
{
    public const string EmailMarker = "[email]";
    public const string DigitsMarker = "[digits]";
    public const string TokenMarker = "[token]";
    public const string SecretMarker = "[secret]";

    private const int DigitRunLength = 10;
    private const int HexIdentifierLength = 16;

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

        // One pattern per kind, each run only when the text holds what every match of it needs; every free-length pattern
        // is linear (Linear). Regex.Replace returns the very instance it was given when nothing matched. Emails go first, so
        // a secret pair's value that is an email still ends as [secret].
        var masked = value;
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

        if (masked.Contains('=') && HoldsSecretKey(masked))
        {
            masked = SecretPair().Replace(masked, "${key}=" + SecretMarker);
        }

        return MaskDigitRuns(masked);
    }

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
    /// Masks the runs of ten or more digits. One pass over the value, token by token (a maximal run of letters and digits):
    /// a token that is a hexadecimal identifier is skipped whole; in any other token each long run is masked unless it lies
    /// in a GUID. The GUIDs are found once per value, by shape, wherever they stand in a dashed run.
    /// </summary>
    private static string MaskDigitRuns(string value)
    {
        StringBuilder? builder = null;
        List<Range>? guids = null;
        var guidIndex = 0;
        var copied = 0;
        var i = 0;
        while (i < value.Length)
        {
            if (!IsTokenChar(value[i]))
            {
                i++;
                continue;
            }

            var tokenStart = i;
            var longestRun = 0;
            var run = 0;
            while (i < value.Length && IsTokenChar(value[i]))
            {
                run = IsDigit(value[i]) ? run + 1 : 0;
                longestRun = Math.Max(longestRun, run);
                i++;
            }

            if (longestRun < DigitRunLength || IsHexIdentifier(value.AsSpan(tokenStart, i - tokenStart)))
            {
                continue;
            }

            for (var j = tokenStart; j < i;)
            {
                if (!IsDigit(value[j]))
                {
                    j++;
                    continue;
                }

                var runStart = j;
                while (j < i && IsDigit(value[j]))
                {
                    j++;
                }

                if (j - runStart < DigitRunLength)
                {
                    continue;
                }

                guids ??= Guids(value);
                while (guidIndex < guids.Count && guids[guidIndex].End.Value <= runStart)
                {
                    guidIndex++;
                }

                if (guidIndex < guids.Count && guids[guidIndex].Start.Value <= runStart && j <= guids[guidIndex].End.Value)
                {
                    continue;
                }

                builder ??= new StringBuilder(value.Length);
                builder.Append(value, copied, runStart - copied).Append(DigitsMarker);
                copied = j;
            }
        }

        return builder is null ? value : builder.Append(value, copied, value.Length - copied).ToString();
    }

    private static bool IsDigit(char c) =>
        char.IsAsciiDigit(c) || c is >= '\u0660' and <= '\u0669' || c is >= '\u06F0' and <= '\u06F9';

    /// <summary>A trace, span or GUID "N" id: sixteen or more hexadecimal characters, at least one of them a letter.</summary>
    private static bool IsHexIdentifier(ReadOnlySpan<char> token) =>
        token.Length >= HexIdentifierLength && !token.ContainsAnyExcept(HexCharacters) && token.ContainsAny(HexLetters);

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
        System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    private static readonly System.Buffers.SearchValues<char> HexLetters =
        System.Buffers.SearchValues.Create("abcdefABCDEF");

    private static readonly System.Buffers.SearchValues<char> SchemeCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ-");

    /// <summary>The words every key of <see cref="SecretPair"/> holds.</summary>
    private static readonly string[] SecretKeys = ["pass", "pwd", "secret", "apikey", "api_key", "api-key"];

    /// <summary>An email address: a local part, <c>@</c> or its URL encoding <c>%40</c>, a dotted domain.</summary>
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+(?:@|%40)[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", Linear)]
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
    /// A secret key-value pair; the key (the whole run of key characters ending in a secret word) keeps its spelling, the
    /// separator becomes a plain <c>=</c>. No word boundary and no lazy prefix: the leftmost match starts at the beginning of
    /// the run by itself.
    /// </summary>
    [GeneratedRegex(@"(?<key>[A-Za-z0-9_.\-]*(?i:password|passwd|pwd|secret|api[_\-]?key))\s*=\s*[^;&\s]*", Linear)]
    private static partial Regex SecretPair();

    /// <summary>The GUID shape; fixed length, so each start is tried over at most 36 characters.</summary>
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidShape();
}
