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
/// IBAN numbers all have ten or more. Groups of two or more digits joined by one space or one hyphen, the same separator
/// throughout, count as one run (<c>055 123 4567</c>, <c>SA03 8000 0000 6080 1016 7519</c>), while a date and time
/// (<c>2026-10-02 12:34:56</c>), an IP address, a version or a list of single digits is kept. A run inside a hexadecimal
/// identifier (a trace or span id: sixteen or more hexadecimal characters with at least one letter) or inside a GUID is
/// kept.</item>
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
    /// Masks the runs of ten or more digits. One pass over the value, token by token (a maximal run of letters and digits),
    /// and within a token group by group (a maximal run of digits). A group in a hexadecimal identifier or in a GUID breaks
    /// any chain and is never masked. Any other group extends the current chain when it follows it after exactly one space
    /// or hyphen, the chain's separator so far, and both it and the chain's last group have two or more digits; otherwise it
    /// starts a new chain. A chain of ten or more digits in all becomes one marker, its separators included.
    /// </summary>
    private static string MaskDigitRuns(string value)
    {
        if (!HoldsDigitRunLength(value))
        {
            return value;
        }

        var runs = new DigitRuns(value);
        var i = 0;
        while (i < value.Length)
        {
            if (!IsTokenChar(value[i]))
            {
                i++;
                continue;
            }

            var tokenStart = i;
            while (i < value.Length && IsTokenChar(value[i]))
            {
                i++;
            }

            var hexIdentifier = IsHexIdentifier(value.AsSpan(tokenStart, i - tokenStart));
            for (var j = tokenStart; j < i;)
            {
                if (!IsDigit(value[j]))
                {
                    j++;
                    continue;
                }

                var groupStart = j;
                while (j < i && IsDigit(value[j]))
                {
                    j++;
                }

                runs.Add(groupStart, j, kept: hexIdentifier);
            }
        }

        return runs.Finish();
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

    /// <summary>The chain of digit groups being built and the masked copy, if any (<see cref="MaskDigitRuns"/>).</summary>
    private sealed class DigitRuns(string value)
    {
        private StringBuilder? _builder;
        private List<Range>? _guids;
        private int _guidIndex;
        private int _copied;
        private int _start = -1;
        private int _end;
        private int _digits;
        private int _lastGroup;
        private char _separator;

        /// <summary>The next group, <c>value[start..end]</c>, in order; <paramref name="kept"/> when its token is a hex identifier.</summary>
        public void Add(int start, int end, bool kept)
        {
            if (kept || InGuid(start, end))
            {
                Flush();
                return;
            }

            var length = end - start;
            if (_start >= 0 && start == _end + 1 && value[_end] is ' ' or '-' && (_separator == '\0' || _separator == value[_end])
                && _lastGroup >= 2 && length >= 2)
            {
                _separator = value[_end];
                _end = end;
                _digits += length;
                _lastGroup = length;
                return;
            }

            Flush();
            _start = start;
            _end = end;
            _digits = length;
            _lastGroup = length;
            _separator = '\0';
        }

        /// <summary>The masked copy, or the value itself when no chain reached ten digits.</summary>
        public string Finish()
        {
            Flush();
            return _builder is null ? value : _builder.Append(value, _copied, value.Length - _copied).ToString();
        }

        private void Flush()
        {
            if (_start >= 0 && _digits >= DigitRunLength)
            {
                _builder ??= new StringBuilder(value.Length);
                _builder.Append(value, _copied, _start - _copied).Append(DigitsMarker);
                _copied = _end;
            }

            _start = -1;
        }

        /// <summary>True when the group lies in a GUID; the GUIDs are found once per value, by shape, on the first call.</summary>
        private bool InGuid(int start, int end)
        {
            _guids ??= Guids(value);
            while (_guidIndex < _guids.Count && _guids[_guidIndex].End.Value <= start)
            {
                _guidIndex++;
            }

            return _guidIndex < _guids.Count && _guids[_guidIndex].Start.Value <= start && end <= _guids[_guidIndex].End.Value;
        }
    }

    /// <summary>A decimal digit of any script (Unicode Nd): Western, Arabic-Indic, Extended Arabic-Indic, Devanagari and so on.</summary>
    private static bool IsDigit(char c) => char.IsDigit(c);

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
    /// or the end of the line), a colon and a bare value (<c>key: value</c>), and <c>key = value</c>. The colon forms keep
    /// their separator (group <c>sep</c>); the last is written back as <c>key=</c>. No word boundary and no lazy prefix: the
    /// leftmost match starts at the beginning of the run by itself.
    /// </summary>
    [GeneratedRegex(@"(?<key>[A-Za-z0-9_.\-]*(?i:password|passwd|pwd|secret|api[_\-]?key))(?:(?<sep>""?\s*:\s*"")[^""\r\n]*|(?<sep>""?\s*:\s*)[^;&\s"",}]*|\s*=\s*[^;&\s]*)", Linear)]
    private static partial Regex SecretPair();

    /// <summary>The GUID shape; fixed length, so each start is tried over at most 36 characters.</summary>
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidShape();
}
