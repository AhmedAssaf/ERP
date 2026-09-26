using Platform.UI;

namespace Platform.UnitTests.Styling;

/// <summary>docs/08 section 3.1: on-primary text colour is computed from the tenant primary's WCAG luminance.</summary>
public class BrandColorsTests
{
    [Theory]
    [InlineData("#0F766E", "#FFFFFF")]
    [InlineData("#1E4E79", "#FFFFFF")]
    [InlineData("#FFFACD", "#111827")]
    [InlineData("#9A3412", "#FFFFFF")]
    public void OnPrimary_picks_white_or_ink_by_contrast(string primary, string expected) =>
        BrandColors.OnPrimary(primary).ShouldBe(expected);

    [Theory]
    [InlineData("#0F766E")]
    [InlineData("#1E4E79")]
    [InlineData("#000000")]
    public void EnsureContrast_keeps_a_passing_colour(string colour) =>
        BrandColors.EnsureContrast(colour, 4.5).ShouldBe(colour);

    [Theory]
    [InlineData("#FFFACD")]
    [InlineData("#FFFFFF")]
    [InlineData("#14B8A6")]
    [InlineData("#fde047")]
    public void EnsureContrast_darkens_a_light_colour_until_it_passes(string colour)
    {
        var adjusted = BrandColors.EnsureContrast(colour, 4.5);

        adjusted.ShouldNotBe(colour.ToUpperInvariant());
        adjusted.ShouldMatch("^#[0-9A-F]{6}$");
        BrandColors.ContrastWithWhite(adjusted).ShouldBeGreaterThanOrEqualTo(4.5);
        BrandColors.OnPrimary(adjusted).ShouldBe("#FFFFFF");
    }

    [Fact]
    public void EnsureContrast_keeps_the_hue_and_stops_at_the_first_passing_step()
    {
        // #14B8A6 (teal) darkens to a darker teal, not to grey or black: two percent lightness at a time.
        var adjusted = BrandColors.EnsureContrast("#14B8A6", 4.5);
        var (r, g, b) = (Convert.ToInt32(adjusted[1..3], 16), Convert.ToInt32(adjusted[3..5], 16), Convert.ToInt32(adjusted[5..7], 16));

        g.ShouldBeGreaterThan(r);
        b.ShouldBeGreaterThan(r);
        BrandColors.ContrastWithWhite(adjusted).ShouldBeLessThan(5.0);
    }
}
