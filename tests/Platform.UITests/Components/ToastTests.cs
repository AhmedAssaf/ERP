using Microsoft.Extensions.DependencyInjection;
using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class ToastTests : ComponentTest
{
    private static readonly string[] FourMessages = ["One", "Two", "Three", "Four"];

    [Fact]
    public void Toast_region_announces_a_message_and_dismisses_it()
    {
        var cut = Render<ToastRegion>();
        var region = cut.Find("[role=status]");
        region.GetAttribute("aria-live").ShouldBe("polite");
        region.GetAttribute("aria-label").ShouldBe("Notifications");

        cut.InvokeAsync(() => Services.GetRequiredService<ToastService>().Show("Branding saved"));

        cut.WaitForAssertion(() => cut.Find("[role=status]").TextContent.ShouldContain("Branding saved"));
        var dismiss = cut.Find("[role=status] button");
        dismiss.GetAttribute("aria-label").ShouldBe("Dismiss message");

        dismiss.Click();

        cut.Find("[role=status]").TextContent.ShouldNotContain("Branding saved");
    }

    [Fact]
    public void Toast_shows_the_message_in_its_tone()
    {
        var cut = Render<Toast>(p => p.Add(t => t.Message, "Invitation failed; try again.").Add(t => t.Tone, ToastTone.Danger));

        cut.Find("div").ClassList.ShouldContain("border-danger");
        cut.Markup.ShouldContain("Invitation failed; try again.");
    }

    [Fact]
    public void The_region_keeps_at_most_three_messages_newest_last()
    {
        var toasts = Services.GetRequiredService<ToastService>();
        var cut = Render<ToastRegion>();

        cut.InvokeAsync(() =>
        {
            foreach (var message in FourMessages)
            {
                toasts.Show(message);
            }
        });

        cut.WaitForAssertion(() => cut.FindAll("[data-toast]").Select(t => t.TextContent.Trim()).ShouldBe(["Two", "Three", "Four"]));
    }
}
