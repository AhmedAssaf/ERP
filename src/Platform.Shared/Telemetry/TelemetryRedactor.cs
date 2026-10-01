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

    /// <summary>Masks every email, token, secret pair and long digit run in <paramref name="value"/>.</summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // One pattern per kind, each run only when the text holds what every match of it needs, so a long value with nothing
        // to mask is never searched by a pattern that would backtrack over it. Regex.Replace returns the very instance it was
        // given when nothing matched. Emails go first, so a secret pair's value that is an email still ends as [secret].
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

        if (masked.Contains('=') && SecretKeys.Any(key => masked.Contains(key, StringComparison.OrdinalIgnoreCase)))
        {
            masked = SecretPair().Replace(masked, "${key}=" + SecretMarker);
        }

        return MaskDigitRuns(masked);
    }

    private static string MaskDigitRuns(string value)
    {
        StringBuilder? builder = null;
        var copied = 0;
        var i = 0;
        while (i < value.Length)
        {
            if (!IsDigit(value[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < value.Length && IsDigit(value[i]))
            {
                i++;
            }

            if (i - start >= DigitRunLength && !InsideIdentifier(value, start, i))
            {
                builder ??= new StringBuilder(value.Length);
                builder.Append(value, copied, start - copied).Append(DigitsMarker);
                copied = i;
            }
        }

        return builder is null ? value : builder.Append(value, copied, value.Length - copied).ToString();
    }

    private static bool IsDigit(char c) =>
        char.IsAsciiDigit(c) || c is >= '٠' and <= '٩' || c is >= '۰' and <= '۹';

    /// <summary>True when the digit run [start, end) lies in a hexadecimal identifier or a GUID, which are never masked.</summary>
    private static bool InsideIdentifier(string value, int start, int end)
    {
        // The alphanumeric token around the run: all hexadecimal, long enough, with a letter (a trace, span or GUID "N" id).
        var tokenStart = start;
        while (tokenStart > 0 && IsTokenChar(value[tokenStart - 1]))
        {
            tokenStart--;
        }

        var tokenEnd = end;
        while (tokenEnd < value.Length && IsTokenChar(value[tokenEnd]))
        {
            tokenEnd++;
        }

        var token = value.AsSpan(tokenStart, tokenEnd - tokenStart);
        if (token.Length >= HexIdentifierLength && !token.ContainsAnyExcept(HexCharacters) && token.ContainsAny(HexLetters))
        {
            return true;
        }

        // The dashed run around it: a GUID (8-4-4-4-12 hexadecimal), whose last group may be twelve digits.
        while (tokenStart > 0 && (IsTokenChar(value[tokenStart - 1]) || value[tokenStart - 1] == '-'))
        {
            tokenStart--;
        }

        while (tokenEnd < value.Length && (IsTokenChar(value[tokenEnd]) || value[tokenEnd] == '-'))
        {
            tokenEnd++;
        }

        return GuidShape().IsMatch(value.AsSpan(tokenStart, tokenEnd - tokenStart));
    }

    private static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || IsDigit(c);

    private static readonly System.Buffers.SearchValues<char> HexCharacters =
        System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    private static readonly System.Buffers.SearchValues<char> HexLetters =
        System.Buffers.SearchValues.Create("abcdefABCDEF");

    /// <summary>The words every key of <see cref="SecretPair"/> holds.</summary>
    private static readonly string[] SecretKeys = ["pass", "pwd", "secret", "apikey", "api_key", "api-key"];

    /// <summary>An email address: a local part, <c>@</c> or its URL encoding <c>%40</c>, a dotted domain.</summary>
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+(?:@|%40)[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    /// <summary>A JWT: a base64url header starting <c>eyJ</c>, a payload and a signature, separated by dots.</summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]*\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    /// <summary>The credential after <c>Bearer</c> (any case); the word and its white space are kept.</summary>
    [GeneratedRegex(@"(?<bearer>\b(?i:bearer)\s+)[A-Za-z0-9\-._~+/]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    /// <summary>A secret key-value pair; the key keeps its spelling, the separator becomes a plain <c>=</c>.</summary>
    [GeneratedRegex(@"(?<key>\b[A-Za-z0-9_.\-]*?(?i:password|passwd|pwd|secret|api[_\-]?key))\s*=\s*[^;&\s]*", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPair();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidShape();
}
