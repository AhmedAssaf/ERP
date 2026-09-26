using Platform.Modules.Identity.Members;

namespace Platform.UnitTests.Identity;

/// <summary>
/// A staff member's display name (F-06): 1 to 100 characters of letters in any script, combining marks, spaces,
/// apostrophes, hyphens and periods. Markup, symbols, controls, and bidi-override or zero-width characters are refused,
/// since the name is shown to other tenants' staff and in emails.
/// </summary>
public class DisplayNamesTests
{
    [Theory]
    [InlineData("Sara Ahmed")]
    [InlineData("سارة أحمد")]
    [InlineData("عبدُ الله")]
    [InlineData("Mary-Jane O'Neil")]
    [InlineData("J. R. Smith")]
    [InlineData("José Ñúñez")]
    [InlineData("A")]
    public void Accepts_names_of_letters_marks_spaces_apostrophes_hyphens_and_periods(string name) =>
        DisplayNames.IsValid(name).ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<b>Sara</b>")]
    [InlineData("Sara & Omar")]
    [InlineData("Sara\u202Edamha")]
    [InlineData("Sara\u202AAhmed")]
    [InlineData("Sara\u2066Ahmed\u2069")]
    [InlineData("Sara\u200BAhmed")]
    [InlineData("Sara\u200FAhmed")]
    [InlineData("\uFEFFSara")]
    [InlineData("Sara\tAhmed")]
    [InlineData("Sara\nAhmed")]
    [InlineData("Sara Ahmed 2")]
    [InlineData("sara@example.com")]
    [InlineData("Sara_Ahmed")]
    public void Refuses_empty_names_markup_symbols_digits_controls_and_invisible_or_bidi_characters(string name) =>
        DisplayNames.IsValid(name).ShouldBeFalse();

    [Fact]
    public void Allows_100_characters_and_refuses_101() =>
        (DisplayNames.IsValid(new string('a', DisplayNames.MaxLength)), DisplayNames.IsValid(new string('a', DisplayNames.MaxLength + 1)))
            .ShouldBe((true, false));

    [Fact]
    public void The_maximum_is_100() => DisplayNames.MaxLength.ShouldBe(100);
}
