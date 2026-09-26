using System.Text.RegularExpressions;

namespace Platform.Shared.Text;

/// <summary>
/// The platform's rule for an email address a person types for someone else to see or use (a staff invitation, F-06; a
/// vendor company's contact, F-11). Stricter than <see cref="System.Net.Mail.MailAddress"/>: ASCII only, an unquoted
/// local part, a domain of at least two letter/digit/hyphen labels (no label starting or ending with a hyphen, no
/// trailing dot, no dotted-quad IP literal), and at most 254 characters overall. The address is shown next to names and
/// put in emails, so the bidi-override and zero-width characters of <see cref="TextSafety"/> must not enter here either,
/// though an ASCII-only address can never carry them.
/// </summary>
public static partial class EmailAddresses
{
    public const int MaxLength = 254;

    /// <summary>The trimmed, lower-cased address when <paramref name="email"/> follows the rule; null otherwise.</summary>
    public static string? Normalize(string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxLength || TextSafety.HasInvisibleOrBidiControl(trimmed))
        {
            return null;
        }

        foreach (var c in trimmed)
        {
            if (c > '\u007F')
            {
                return null;
            }
        }

        var at = trimmed.IndexOf('@');
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
        {
            return null;
        }

        var localPart = trimmed[..at];
        var domain = trimmed[(at + 1)..];
        if (localPart[0] == '"' || !LocalPart().IsMatch(localPart))
        {
            return null;
        }

        var labels = domain.Split('.');
        if (labels.Length < 2 || labels.Any(label => !DomainLabel().IsMatch(label))
            || labels.All(label => label.All(char.IsAsciiDigit)))
        {
            // Fewer than two labels (no dot, "localhost"), an empty label (a trailing or doubled dot), a label
            // starting or ending with a hyphen, or every label numeric (a dotted-quad IP literal) is refused.
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPart();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainLabel();
}
