using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class ColorFieldTests : ComponentTest
{
    [Theory]
    [InlineData("#12345G")]
    [InlineData("123456")]
    [InlineData("#FFF")]
    [InlineData("red")]
    [InlineData("#1234567")]
    [InlineData("")]
    public void ColorField_rejects_an_invalid_hex(string typed)
    {
        var raised = new List<string>();
        var cut = Render<ColorField>(p => p
            .Add(f => f.Label, "Primary colour")
            .Add(f => f.Value, "#1E4E79")
            .Add(f => f.ValueChanged, v => raised.Add(v)));

        cut.Find("input[type=text]").Change(typed);

        raised.ShouldBeEmpty();
        var text = cut.Find("input[type=text]");
        text.GetAttribute("aria-invalid").ShouldBe("true");
        text.GetAttribute("value").ShouldBe(typed);
        var error = cut.Find($"#{text.GetAttribute("aria-describedby")}");
        error.TextContent.ShouldBe("Enter the colour as # followed by six hexadecimal digits, for example #1E4E79.");
    }

    [Fact]
    public void ColorField_accepts_a_valid_hex_from_either_input_in_upper_case()
    {
        var raised = new List<string>();
        var cut = Render<ColorField>(p => p
            .Add(f => f.Label, "Primary colour")
            .Add(f => f.Value, "#1E4E79")
            .Add(f => f.ValueChanged, v => raised.Add(v)));

        cut.Find("input[type=text]").Change("#12345G");
        cut.Find("input[type=text]").Change(" #0f766e ");
        cut.Find("input[type=color]").Change("#112233");

        raised.ShouldBe(["#0F766E", "#112233"]);
        cut.Find("input[type=text]").HasAttribute("aria-invalid").ShouldBeFalse();
    }

    [Fact]
    public void ColorField_labels_both_inputs()
    {
        var cut = Render<ColorField>(p => p.Add(f => f.Label, "Primary colour").Add(f => f.Value, "#1E4E79"));

        var text = cut.Find("input[type=text]");
        cut.Find("label").GetAttribute("for").ShouldBe(text.Id);
        var picker = cut.Find("input[type=color]");
        picker.GetAttribute("aria-label").ShouldBe("Primary colour (colour picker)");
        picker.GetAttribute("value").ShouldBe("#1e4e79");
    }
}
