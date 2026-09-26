using Platform.UI.Components;
using Platform.UITests.Fixtures;

namespace Platform.UITests.Components;

public class AuditListTests : ComponentTest
{
    private static readonly AuditEntry[] Entries =
    [
        new("Sara Ahmed", "Invited Omar Khalid as contracts officer", new DateTimeOffset(2026, 9, 26, 11, 5, 0, TimeSpan.Zero)),
        new("Omar Khalid", "Accepted the invitation", new DateTimeOffset(2026, 9, 25, 22, 30, 0, TimeSpan.Zero), "From the email link"),
    ];

    [Fact]
    public void AuditList_shows_actor_action_and_riyadh_time_in_the_given_order()
    {
        var cut = Render<AuditList>(p => p.Add(a => a.Entries, Entries).Add(a => a.Label, "Staff history"));

        cut.Find("ol").GetAttribute("aria-label").ShouldBe("Staff history");
        var items = cut.FindAll("li");
        items.Count.ShouldBe(2);
        items[0].TextContent.ShouldContain("Sara Ahmed");
        items[0].TextContent.ShouldContain("Invited Omar Khalid as contracts officer");
        var time = items[0].QuerySelector("time")!;
        time.GetAttribute("datetime").ShouldBe("2026-09-26T14:05:00+03:00");
        time.TextContent.ShouldBe("26 September 2026, 14:05");
        items[1].QuerySelector("time")!.TextContent.ShouldBe("26 September 2026, 01:30");
        items[1].TextContent.ShouldContain("From the email link");
        cut.Markup.ShouldContain("Times are in Riyadh time.");
    }

    [Fact]
    public void AuditList_uses_gregorian_months_in_arabic()
    {
        UseCulture("ar-SA");

        var cut = Render<AuditList>(p => p.Add(a => a.Entries, Entries).Add(a => a.Label, "سجل الموظفين"));

        cut.FindAll("time")[0].TextContent.ShouldBe("26 سبتمبر 2026، 14:05");
    }

    [Fact]
    public void AuditList_says_when_nothing_has_happened_yet()
    {
        var cut = Render<AuditList>(p => p.Add(a => a.Entries, []).Add(a => a.Label, "Staff history"));

        cut.FindAll("li").ShouldBeEmpty();
        cut.Markup.ShouldContain("No events have been recorded yet.");
    }
}
