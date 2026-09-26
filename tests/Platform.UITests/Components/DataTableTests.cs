using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class DataTableTests : ComponentTest
{
    private static readonly IQueryable<HealthRow> Rows = new[] { new HealthRow("Database", 12), new HealthRow("Object storage", 7) }.AsQueryable();

    [Fact]
    public void DataTable_renders_headers_and_rows()
    {
        var cut = Render<HealthTable>(p => p.Add(t => t.Items, Rows));

        cut.FindAll("thead th").Select(th => th.TextContent.Trim()).ShouldBe(["Component", "Checks"]);
        var rows = cut.FindAll("tbody tr");
        rows.Count.ShouldBe(2);
        rows[0].QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ShouldBe(["Database", "12"]);
        rows[1].QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ShouldBe(["Object storage", "7"]);

        var region = cut.Find("[role=region]");
        region.GetAttribute("aria-label").ShouldBe("Component health");
        region.GetAttribute("tabindex").ShouldBe("0");
        region.ClassList.ShouldContain("overflow-auto");
        cut.Find("table").ClassList.ShouldContain("tabular-nums");
        cut.Find("table").ClassList.ShouldContain("[&_thead_th]:sticky");
    }

    [Fact]
    public void DataTable_density_toggle_compacts_rows()
    {
        var cut = Render<HealthTable>(p => p.Add(t => t.Items, Rows));
        var toggle = cut.Find("button[aria-pressed]");
        toggle.TextContent.Trim().ShouldBe("Compact rows");
        toggle.GetAttribute("aria-pressed").ShouldBe("false");
        cut.Find("[data-density]").GetAttribute("data-density").ShouldBe("comfortable");

        toggle.Click();

        cut.Find("button[aria-pressed]").GetAttribute("aria-pressed").ShouldBe("true");
        cut.Find("[data-density]").GetAttribute("data-density").ShouldBe("compact");
    }

    [Fact]
    public void DataTable_starts_compact_when_asked()
    {
        var cut = Render<HealthTable>(p => p.Add(t => t.Items, Rows).Add(t => t.Density, TableDensity.Compact));

        cut.Find("[data-density]").GetAttribute("data-density").ShouldBe("compact");
    }

    [Fact]
    public void DataTable_shows_the_empty_content_when_there_are_no_rows()
    {
        var cut = Render<HealthTable>(p => p
            .Add(t => t.Items, Array.Empty<HealthRow>().AsQueryable())
            .Add(t => t.EmptyContent, "<p id=\"nothing\">No checks have run yet.</p>"));

        cut.FindAll("table").ShouldBeEmpty();
        cut.Find("#nothing").TextContent.ShouldBe("No checks have run yet.");
    }

    [Fact]
    public void DataTable_says_there_are_no_rows_without_empty_content()
    {
        var cut = Render<HealthTable>(p => p.Add(t => t.Items, Array.Empty<HealthRow>().AsQueryable()));

        cut.FindAll("table").ShouldBeEmpty();
        cut.Markup.ShouldContain("There are no rows to show.");
    }
}
