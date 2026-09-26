using System.Globalization;

namespace Platform.Shared.Branding;

/// <summary>
/// WCAG 2.x contrast for tenant colours (F-02, spec D-11): the Tenancy module enforces it when branding is saved and
/// <c>Platform.UI.BrandColors</c> uses it for the on-primary text colour. Colours are <c>#RRGGBB</c>.
/// </summary>
public static class ColorContrast
{
    /// <summary>The lightness step of <see cref="EnsureContrast"/>: two percentage points of HSL lightness.</summary>
    public const double LightnessStep = 0.02;

    private static readonly double WhiteLuminance = RelativeLuminance(1, 1, 1);

    /// <summary>The contrast ratio of the colour with white, from 1 to 21.</summary>
    public static double WithWhite(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        return Ratio(RelativeLuminance(r, g, b), WhiteLuminance);
    }

    /// <summary>
    /// The colour itself, in upper case, when its contrast with white is at least <paramref name="minimumRatio"/>;
    /// otherwise the same hue and saturation darkened in <see cref="LightnessStep"/> steps of HSL lightness until it is.
    /// Black always passes, so this ends.
    /// </summary>
    public static string EnsureContrast(string hex, double minimumRatio)
    {
        var (r, g, b) = ParseRgb(hex);
        if (Ratio(RelativeLuminance(r, g, b), WhiteLuminance) >= minimumRatio)
        {
            return Format(r, g, b);
        }

        var (h, s, l) = ToHsl(r, g, b);
        while (true)
        {
            l = Math.Max(0, l - LightnessStep);
            var (nr, ng, nb) = Quantize(FromHsl(h, s, l));
            if (l <= 0 || Ratio(RelativeLuminance(nr, ng, nb), WhiteLuminance) >= minimumRatio)
            {
                return Format(nr, ng, nb);
            }
        }
    }

    // WCAG 2.x contrast ratio: (L1 + 0.05) / (L2 + 0.05) with L1 the lighter of the two.
    private static double Ratio(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    // WCAG 2.x relative luminance from sRGB channels in 0..1.
    private static double RelativeLuminance(double r, double g, double b) =>
        0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);

    private static double Linearize(double channel) =>
        channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    // The colour as it will be stored: whole bytes, so the ratio checked is the ratio of the saved value.
    private static (double R, double G, double B) Quantize((double R, double G, double B) c) =>
        (Math.Round(c.R * 255) / 255, Math.Round(c.G * 255) / 255, Math.Round(c.B * 255) / 255);

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9)
        {
            return (0, 0, l);
        }

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r)
        {
            h = ((g - b) / d) + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            h = ((b - r) / d) + 2;
        }
        else
        {
            h = ((r - g) / d) + 4;
        }

        return (h / 6, s, l);
    }

    private static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        if (s < 1e-9)
        {
            return (l, l, l);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        return (Hue(p, q, h + (1.0 / 3)), Hue(p, q, h), Hue(p, q, h - (1.0 / 3)));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        return t switch
        {
            < 1.0 / 6 => p + ((q - p) * 6 * t),
            < 1.0 / 2 => q,
            < 2.0 / 3 => p + ((q - p) * ((2.0 / 3) - t) * 6),
            _ => p,
        };
    }

    private static string Format(double r, double g, double b) => string.Create(
        CultureInfo.InvariantCulture, $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}");

    private static (double R, double G, double B) ParseRgb(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var span = hex.AsSpan().Trim().TrimStart('#');
        if (span.Length != 6)
        {
            throw new FormatException("A colour is # followed by six hexadecimal digits.");
        }

        var r = byte.Parse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = byte.Parse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = byte.Parse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (r / 255.0, g / 255.0, b / 255.0);
    }
}
