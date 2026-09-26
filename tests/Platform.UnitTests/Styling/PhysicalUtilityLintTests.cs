namespace Platform.UnitTests.Styling;

/// <summary>W-05 and docs/08 section 4: physical direction utilities are banned in favour of logical ones.</summary>
public class PhysicalUtilityLintTests
{
    [Fact]
    public void A_razor_file_with_ml_4_is_reported_with_file_and_line()
    {
        var folder = Directory.CreateTempSubdirectory("lint-");
        try
        {
            File.WriteAllLines(Path.Combine(folder.FullName, "Sample.razor"), ["<div class=\"ms-2\">", "<p class=\"ml-4 text-start\">x</p>", "</div>"]);

            var findings = PhysicalUtilityLint.Scan(folder.FullName).ToList();

            findings.ShouldBe(["Sample.razor:2: ml-4"]);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("pr-6")]
    [InlineData("text-right")]
    [InlineData("left-0")]
    [InlineData("rounded-l-md")]
    [InlineData("border-r-2")]
    [InlineData("rtl:mr-2")]
    [InlineData("-ml-4")]
    [InlineData("-ml-[3px]")]
    [InlineData("hover:-mr-2")]
    [InlineData("-left-2")]
    [InlineData("md:pr-2")]
    [InlineData("ml-[3px]")]
    public void Physical_utilities_are_detected(string utility) =>
        PhysicalUtilityLint.FindIn($"<div class=\"{utility}\"></div>").ShouldBe([utility.Split(':')[^1]]);

    [Theory]
    [InlineData("ms-4 me-2 ps-1 pe-3 start-0 end-0 text-start text-end rounded-s-md border-e-2")]
    [InlineData("px-6 mx-auto")]
    [InlineData("border-left-color copyright-notice html-body")]
    [InlineData("-ms-4 -me-2")]
    public void Logical_and_unrelated_text_is_not_reported(string text) =>
        PhysicalUtilityLint.FindIn(text).ShouldBeEmpty();

    [Fact]
    public void The_source_tree_has_no_physical_utilities() =>
        PhysicalUtilityLint.Scan(TestRepo.Src).ShouldBeEmpty();
}
