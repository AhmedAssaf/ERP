using Platform.Modules.Vendors.Registration;

namespace Platform.UnitTests.Vendors;

/// <summary>
/// W-33 second review: ownership notes and dispute statements come from multi-line fields, so line breaks are allowed
/// (CRLF and lone CR stored as LF, the length counted after that); every other control, separator, invisible or bidi
/// character stays refused.
/// </summary>
public sealed class VendorFreeTextTests
{
    [Theory]
    [InlineData("First line.\r\nSecond line.", "First line.\nSecond line.")]
    [InlineData("First line.\nSecond line.", "First line.\nSecond line.")]
    [InlineData("First line.\rSecond line.", "First line.\nSecond line.")]
    [InlineData("  Padded.\r\n\r\n  ", "Padded.")]
    public void Line_breaks_are_allowed_and_stored_as_line_feeds(string value, string stored)
    {
        VendorInput.IsFreeText(value, 100).ShouldBeTrue();
        VendorInput.NormalizeFreeText(value).ShouldBe(stored);
    }

    [Theory]
    [InlineData("Tab\there")]
    [InlineData("Bell\u0007here")]
    [InlineData("Null\0here")]
    [InlineData("Line\u2028separator")]
    [InlineData("Paragraph\u2029separator")]
    [InlineData("Right\u202Eto left")]
    [InlineData("Zero\u200Bwidth")]
    [InlineData("<script>")]
    public void Other_control_separator_invisible_and_markup_characters_stay_refused(string value)
    {
        VendorInput.IsFreeText(value, 100).ShouldBeFalse();
    }

    [Fact]
    public void The_length_is_counted_after_line_breaks_are_normalised()
    {
        // 500 characters and 499 CRLF pairs: 1,498 characters as typed, 999 as stored.
        var typed = string.Join("\r\n", Enumerable.Repeat("x", 500));
        typed.Length.ShouldBe(1498);

        VendorInput.NormalizeFreeText(typed).Length.ShouldBe(999);
        VendorInput.IsFreeText(typed, 1000).ShouldBeTrue();
        VendorInput.IsFreeText(typed + "\r\nxx", 1000).ShouldBeFalse();
    }
}
