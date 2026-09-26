using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class ButtonTests : ComponentTest
{
    [Fact]
    public void Button_shows_loading_and_is_disabled()
    {
        var clicks = 0;
        var cut = Render<Button>(p => p
            .Add(b => b.Loading, true)
            .Add(b => b.OnClick, () => clicks++)
            .AddChildContent("Publish tender"));

        var button = cut.Find("button");
        button.HasAttribute("disabled").ShouldBeTrue();
        button.GetAttribute("aria-busy").ShouldBe("true");
        button.TextContent.ShouldContain("Publish tender");
        button.TextContent.ShouldContain("Working, please wait");
        cut.FindAll("[data-spinner]").Count.ShouldBe(1);

        button.Click();
        clicks.ShouldBe(0);
    }

    [Theory]
    [InlineData(ButtonVariant.Primary, "bg-action")]
    [InlineData(ButtonVariant.Secondary, "bg-surface")]
    [InlineData(ButtonVariant.Danger, "bg-danger")]
    [InlineData(ButtonVariant.Quiet, "text-action")]
    public void Button_raises_click_and_carries_its_variant(ButtonVariant variant, string expectedClass)
    {
        var clicks = 0;
        var cut = Render<Button>(p => p
            .Add(b => b.Variant, variant)
            .Add(b => b.OnClick, () => clicks++)
            .AddChildContent("Save draft"));

        var button = cut.Find("button");
        button.ClassList.ShouldContain(expectedClass);
        button.GetAttribute("type").ShouldBe("button");
        button.HasAttribute("disabled").ShouldBeFalse();
        cut.FindAll("[data-spinner]").ShouldBeEmpty();

        button.Click();
        clicks.ShouldBe(1);
    }

    [Fact]
    public void Button_can_submit_a_form()
    {
        var cut = Render<Button>(p => p.Add(b => b.Type, "submit").AddChildContent("Save branding"));

        cut.Find("button").GetAttribute("type").ShouldBe("submit");
    }
}
