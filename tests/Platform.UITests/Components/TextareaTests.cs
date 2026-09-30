using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

/// <summary>The multi-line field (docs/08 section 6) behaves as TextField does: label, help and error tied to it.</summary>
public class TextareaTests : ComponentTest
{
    [Fact]
    public void Textarea_ties_label_help_and_error_to_the_field_and_shows_its_value()
    {
        var cut = Render<Textarea>(p => p
            .Add(f => f.Label, "What you checked")
            .Add(f => f.Help, "Up to 1,000 characters.")
            .Add(f => f.Error, "Write what you checked.")
            .Add(f => f.Required, true)
            .Add(f => f.Value, "The letter is signed by the owner.")
            .Add(f => f.Rows, 3));

        var area = cut.Find("textarea");
        cut.Find("label").GetAttribute("for").ShouldBe(area.Id);
        area.TextContent.ShouldBe("The letter is signed by the owner.");
        area.GetAttribute("rows").ShouldBe("3");
        area.HasAttribute("required").ShouldBeTrue();
        area.GetAttribute("aria-invalid").ShouldBe("true");
        var describedBy = area.GetAttribute("aria-describedby")!.Split(' ');
        cut.Find($"#{describedBy[0]}").TextContent.ShouldBe("Up to 1,000 characters.");
        cut.Find($"#{describedBy[1]}").TextContent.ShouldBe("Write what you checked.");
    }

    [Fact]
    public void Textarea_raises_the_typed_value_with_line_breaks()
    {
        string? value = null;
        var cut = Render<Textarea>(p => p.Add(f => f.Label, "Statement").Add(f => f.ValueChanged, v => value = v));

        cut.Find("textarea").HasAttribute("aria-invalid").ShouldBeFalse();
        cut.Find("textarea").Change("First line.\nSecond line.");

        value.ShouldBe("First line.\nSecond line.");
    }
}
