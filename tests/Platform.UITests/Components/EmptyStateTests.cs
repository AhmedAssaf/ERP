using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class EmptyStateTests : ComponentTest
{
    [Fact]
    public void EmptyState_says_what_to_do_next_with_one_action()
    {
        var cut = Render<EmptyState>(p => p
            .Add(e => e.Title, "No staff invited yet")
            .AddChildContent("Invite the first member of your team to start working on tenders.")
            .Add(e => e.Action, "<button id=\"invite\">Invite staff member</button>"));

        cut.Find("h2").TextContent.ShouldBe("No staff invited yet");
        cut.Markup.ShouldContain("Invite the first member of your team to start working on tenders.");
        cut.Find("#invite").TextContent.ShouldBe("Invite staff member");
    }

    [Fact]
    public void EmptyState_heading_level_can_be_changed()
    {
        var cut = Render<EmptyState>(p => p.Add(e => e.Title, "No incidents").Add(e => e.HeadingLevel, 3));

        cut.Find("h3").TextContent.ShouldBe("No incidents");
    }
}
