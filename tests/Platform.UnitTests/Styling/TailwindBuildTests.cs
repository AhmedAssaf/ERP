namespace Platform.UnitTests.Styling;

public class TailwindBuildTests
{
    [Fact]
    public void Built_css_declares_the_primary_colour_token()
    {
        var css = Path.Combine(TestRepo.Src, "UI", "Platform.UI", "wwwroot", "css", "app.css");

        File.Exists(css).ShouldBeTrue("the Tailwind target in Platform.UI writes this file during the build");
        File.ReadAllText(css).ShouldContain("--color-primary:");
    }
}
