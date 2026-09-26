using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class TextFieldTests : ComponentTest
{
    [Fact]
    public void TextField_ties_label_help_and_error_to_the_input()
    {
        var cut = Render<TextField>(p => p
            .Add(f => f.Label, "Portal name")
            .Add(f => f.Help, "Shown in the header and in emails.")
            .Add(f => f.Error, "Enter a portal name.")
            .Add(f => f.Required, true));

        var input = cut.Find("input");
        var label = cut.Find("label");
        label.GetAttribute("for").ShouldBe(input.Id);
        label.TextContent.ShouldContain("Portal name");
        label.TextContent.ShouldContain("(required)");
        input.HasAttribute("required").ShouldBeTrue();
        input.GetAttribute("aria-invalid").ShouldBe("true");

        var describedBy = input.GetAttribute("aria-describedby")!.Split(' ');
        describedBy.Length.ShouldBe(2);
        cut.Find($"#{describedBy[0]}").TextContent.ShouldBe("Shown in the header and in emails.");
        cut.Find($"#{describedBy[1]}").TextContent.ShouldBe("Enter a portal name.");
    }

    [Fact]
    public void TextField_raises_the_typed_value_and_has_no_error_state_without_an_error()
    {
        string? value = null;
        var cut = Render<TextField>(p => p
            .Add(f => f.Label, "Portal name")
            .Add(f => f.Value, "Acme")
            .Add(f => f.ValueChanged, v => value = v));

        var input = cut.Find("input");
        input.GetAttribute("value").ShouldBe("Acme");
        input.HasAttribute("aria-invalid").ShouldBeFalse();
        input.HasAttribute("aria-describedby").ShouldBeFalse();

        input.Change("Acme Contracting");
        value.ShouldBe("Acme Contracting");
    }

    [Fact]
    public void Two_fields_never_share_an_id()
    {
        var first = Render<TextField>(p => p.Add(f => f.Label, "First"));
        var second = Render<TextField>(p => p.Add(f => f.Label, "Second"));

        first.Find("input").Id.ShouldNotBe(second.Find("input").Id);
    }
}
