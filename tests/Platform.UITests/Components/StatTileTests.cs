using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

/// <summary>
/// W-10 console usage page (spec 6.6): one number with its label and, optionally, its parts on their own lines. An unknown
/// number is a dash named "Unknown" for assistive technology, never a guess (as the console's other dashes, D-12).
/// </summary>
public class StatTileTests : ComponentTest
{
    [Fact]
    public void StatTile_shows_its_label_value_and_split()
    {
        var cut = Render<StatTile>(p => p
            .Add(t => t.Label, "Online now")
            .Add(t => t.Value, 1234)
            .Add(t => t.Split, [new StatTilePart("Staff", 1200), new StatTilePart("Vendors", 34)])
            .AddUnmatched("data-tile", "online"));

        var tile = cut.Find("[data-tile=online]");
        tile.QuerySelector("h3")!.TextContent.Trim().ShouldBe("Online now");
        var value = tile.QuerySelector("[data-value]")!;
        value.GetAttribute("data-value").ShouldBe("1234");
        value.TextContent.Trim().ShouldBe("1,234");
        var parts = tile.QuerySelectorAll("dt").Select(d => d.TextContent.Trim()).ToList();
        parts.ShouldBe(["Staff", "Vendors"]);
        tile.QuerySelectorAll("dd").Select(d => d.GetAttribute("data-value")).ShouldBe(["1200", "34"]);
        tile.OuterHtml.ShouldNotContain("uppercase");
    }

    [Fact]
    public void StatTile_without_a_value_shows_a_dash_named_unknown()
    {
        var cut = Render<StatTile>(p => p
            .Add(t => t.Label, "Active today")
            .Add(t => t.Value, null)
            .Add(t => t.Split, [new StatTilePart("Staff", null)]));

        var value = cut.Find("[data-value]");
        value.GetAttribute("data-value").ShouldBe(string.Empty);
        value.TextContent.Trim().ShouldBe("—");
        value.QuerySelector("[aria-label]")!.GetAttribute("aria-label").ShouldBe("Unknown");
        var part = cut.Find("dd");
        part.GetAttribute("data-value").ShouldBe(string.Empty);
        part.QuerySelector("[aria-label]")!.GetAttribute("aria-label").ShouldBe("Unknown");
    }

    [Fact]
    public void StatTile_without_a_split_shows_no_list()
    {
        var cut = Render<StatTile>(p => p.Add(t => t.Label, "Circuits").Add(t => t.Value, 0));

        cut.Find("[data-value]").TextContent.Trim().ShouldBe("0");
        cut.FindAll("dl").ShouldBeEmpty();
    }

    [Fact]
    public void StatTile_names_unknown_in_Arabic_too()
    {
        UseCulture("ar-SA");

        var cut = Render<StatTile>(p => p.Add(t => t.Label, "النشطون اليوم").Add(t => t.Value, null));

        cut.Find("[data-value] [aria-label]").GetAttribute("aria-label").ShouldBe("غير معروف");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void StatTile_heading_level_fits_the_page_outline(int level)
    {
        var cut = Render<StatTile>(p => p.Add(t => t.Label, "Online now").Add(t => t.Value, 1).Add(t => t.HeadingLevel, level));

        cut.Find($"h{level}").TextContent.Trim().ShouldBe("Online now");
    }
}
