using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class StatusBadgeTests : ComponentTest
{
    [Theory]
    [InlineData(StatusTone.Neutral, "text-ink-muted")]
    [InlineData(StatusTone.Info, "text-action")]
    [InlineData(StatusTone.Success, "text-success")]
    [InlineData(StatusTone.Warning, "text-warning")]
    [InlineData(StatusTone.Danger, "text-danger")]
    public void StatusBadge_shows_its_text_in_the_tone_colour_without_uppercase(StatusTone tone, string expectedClass)
    {
        var cut = Render<StatusBadge>(p => p.Add(b => b.Tone, tone).AddChildContent("Healthy"));

        var badge = cut.Find("span");
        badge.TextContent.Trim().ShouldBe("Healthy");
        badge.ClassList.ShouldContain(expectedClass);
        badge.ClassList.ShouldContain("text-micro");
        badge.ClassList.ShouldNotContain("uppercase");
        badge.QuerySelector("[aria-hidden=true]").ShouldNotBeNull();
    }
}
