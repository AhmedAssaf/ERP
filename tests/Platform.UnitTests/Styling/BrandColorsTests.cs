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
}
