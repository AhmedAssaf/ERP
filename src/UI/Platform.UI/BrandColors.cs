using Platform.Shared.Branding;

namespace Platform.UI;

/// <summary>Colour maths for the white-label token set (docs/08 section 3.1), over <see cref="ColorContrast"/>.</summary>
public static class BrandColors
{
    private const string White = "#FFFFFF";
    private const string Ink = "#111827";

    /// <summary>
    /// Picks the on-primary text colour for a tenant's primary colour: white when it gives at least
    /// a 4.5:1 WCAG contrast ratio against the primary, otherwise the ink colour.
    /// </summary>
    public static string OnPrimary(string primaryHex) => ColorContrast.WithWhite(primaryHex) >= 4.5 ? White : Ink;

    /// <summary>The WCAG contrast ratio of the colour with white.</summary>
    public static double ContrastWithWhite(string hex) => ColorContrast.WithWhite(hex);

    /// <summary>
    /// The colour, upper-cased, when its contrast with white reaches <paramref name="minimumRatio"/>; otherwise darkened
    /// in HSL lightness steps of two percent until it does (F-02, spec D-11).
    /// </summary>
    public static string EnsureContrast(string hex, double minimumRatio) => ColorContrast.EnsureContrast(hex, minimumRatio);
}
