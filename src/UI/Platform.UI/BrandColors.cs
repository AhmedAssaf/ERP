using System.Globalization;

namespace Platform.UI;

/// <summary>Colour maths for the white-label token set (docs/08 section 3.1).</summary>
public static class BrandColors
{
    private const string White = "#FFFFFF";
    private const string Ink = "#111827";

    /// <summary>
    /// Picks the on-primary text colour for a tenant's primary colour: white when it gives at least
    /// a 4.5:1 WCAG contrast ratio against the primary, otherwise the ink colour.
    /// </summary>
    public static string OnPrimary(string primaryHex) =>
        ContrastRatio(RelativeLuminance(primaryHex), WhiteLuminance) >= 4.5 ? White : Ink;

    private static readonly double WhiteLuminance = RelativeLuminance(White);

    // WCAG 2.x contrast ratio: (L1 + 0.05) / (L2 + 0.05) with L1 the lighter of the two.
    private static double ContrastRatio(double primaryLuminance, double otherLuminance)
    {
        var lighter = Math.Max(primaryLuminance, otherLuminance);
        var darker = Math.Min(primaryLuminance, otherLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    // WCAG 2.x relative luminance from sRGB channels.
    private static double RelativeLuminance(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
    }

    private static double Linearize(double channel) =>
        channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static (double R, double G, double B) ParseRgb(string hex)
    {
        var span = hex.AsSpan().TrimStart('#');
        var r = byte.Parse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = byte.Parse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = byte.Parse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (r / 255.0, g / 255.0, b / 255.0);
    }
}
