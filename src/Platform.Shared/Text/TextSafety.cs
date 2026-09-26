using System.Globalization;
using System.Text;

namespace Platform.Shared.Text;

/// <summary>
/// The invisible and bidi-control rule shared by every user-supplied value that is later displayed to someone else: a
/// staff display name (F-06), an email address (F-06) and a tenant's portal name (F-02). Characters in this range can
/// make a value display as something other than what was typed (bidirectional overrides, zero-width joiners and
/// separators, byte-order marks), so they are refused wherever they could reach another person's screen.
/// </summary>
public static class TextSafety
{
    /// <summary>
    /// True when <paramref name="value"/> contains a bidi-override or zero-width character (U+200B-U+200F,
    /// U+202A-U+202E, U+2066-U+2069, U+FEFF) or any other <see cref="UnicodeCategory.Format"/> character.
    /// </summary>
    public static bool HasInvisibleOrBidiControl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsInvisibleOrBidi(rune.Value) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInvisibleOrBidi(int c) =>
        c is (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0xFEFF;
}
