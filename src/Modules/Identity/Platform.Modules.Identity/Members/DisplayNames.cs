using System.Globalization;
using System.Text;

namespace Platform.Modules.Identity.Members;

/// <summary>
/// A staff member's display name (F-06): shown to the tenant's staff, put in emails and sent to Keycloak as first and
/// last name, so only what a person's name needs is accepted: 1 to <see cref="MaxLength"/> characters of letters in any
/// script, combining marks, spaces, apostrophes, hyphens and periods. Everything else is refused, including markup,
/// symbols, digits, controls, and the bidi-override and zero-width characters that could make a name display as
/// something else (U+200B to U+200F, U+202A to U+202E, U+2066 to U+2069, U+FEFF).
/// </summary>
internal static class DisplayNames
{
    public const int MaxLength = 100;

    /// <summary>True when <paramref name="name"/>, already trimmed, is an acceptable display name.</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxLength)
        {
            return false;
        }

        foreach (var rune in name.EnumerateRunes())
        {
            if (IsInvisibleOrBidi(rune.Value) || !IsAllowed(rune))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInvisibleOrBidi(int c) =>
        c is (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0xFEFF;

    // Letters and marks by rune, so a letter outside the Basic Multilingual Plane counts as one letter, not two surrogates.
    private static bool IsAllowed(Rune rune) =>
        rune.Value is ' ' or '\'' or '-' or '.'
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
}
