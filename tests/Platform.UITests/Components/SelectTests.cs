using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class SelectTests : ComponentTest
{
    [Fact]
    public void Select_lists_options_and_raises_the_chosen_value()
    {
        string? value = null;
        var cut = Render<Select>(p => p
            .Add(s => s.Label, "Role")
            .Add(s => s.Options, [new SelectOption("tenant-admin", "Tenant administrator"), new SelectOption("contracts-officer", "Contracts officer")])
            .Add(s => s.Value, "contracts-officer")
            .Add(s => s.ValueChanged, v => value = v));

        var select = cut.Find("select");
        cut.Find("label").GetAttribute("for").ShouldBe(select.Id);
        var options = cut.FindAll("option");
        options.Select(o => o.TextContent).ShouldBe(["Tenant administrator", "Contracts officer"]);
        options[1].HasAttribute("selected").ShouldBeTrue();

        select.Change("tenant-admin");
        value.ShouldBe("tenant-admin");
    }

    [Fact]
    public void Select_shows_a_placeholder_option_when_nothing_is_chosen()
    {
        var cut = Render<Select>(p => p
            .Add(s => s.Label, "Role")
            .Add(s => s.Placeholder, "Choose a role")
            .Add(s => s.Options, [new SelectOption("tenant-admin", "Tenant administrator")]));

        var first = cut.FindAll("option")[0];
        first.TextContent.ShouldBe("Choose a role");
        first.GetAttribute("value").ShouldBe(string.Empty);
        first.HasAttribute("disabled").ShouldBeTrue();
        first.HasAttribute("selected").ShouldBeTrue();
    }
}
