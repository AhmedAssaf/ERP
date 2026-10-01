using Platform.Shared.Telemetry;

namespace Platform.UnitTests.Shared;

/// <summary>
/// W-10, plan task 3 (spec O-10, section 7.1): the one redactor both the log pipeline and the span processor use. A match is
/// replaced with a fixed marker, so a reader sees that something was removed; trace ids, span ids and GUIDs are kept.
/// </summary>
public sealed class TelemetryRedactorTests
{
    [Theory]
    [InlineData("Invitation sent to ahmad@example.sa today.", "Invitation sent to [email] today.")]
    [InlineData("تم إرسال الدعوة إلى ahmad@example.sa اليوم", "تم إرسال الدعوة إلى [email] اليوم")]
    [InlineData("GET admin/realms/waslabid/users?email=ahmad%40example.sa&exact=true", "GET admin/realms/waslabid/users?email=[email]&exact=true")]
    [InlineData("Company with CR 1010123456 registered", "Company with CR [digits] registered")]
    [InlineData("رقم الإقامة 2123456789 غير صالح", "رقم الإقامة [digits] غير صالح")]
    [InlineData("Contact phone 0551234567.", "Contact phone [digits].")]
    [InlineData("IBAN SA0380000000608010167519 refused", "IBAN SA[digits] refused")]
    [InlineData(
        "token eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMDEwMTIzNDU2In0.c2lnbmF0dXJlLXZhbHVl expired",
        "token [token] expired")]
    [InlineData("Authorization: Bearer abc.DEF-ghi_123~", "Authorization: Bearer [token]")]
    [InlineData("Host=db;Username=erp_app;Password=abc;Database=erp", "Host=db;Username=erp_app;Password=[secret];Database=erp")]
    [InlineData("pwd=x&apikey=y client_secret=z", "pwd=[secret]&apikey=[secret] client_secret=[secret]")]
    [InlineData("PASSWORD = hunter2 next", "PASSWORD=[secret] next")]
    [InlineData("user ahmad@example.sa password=ahmad@example.sa", "user [email] password=[secret]")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln", "Authorization: Bearer [token]")]
    [InlineData("PublicKeyToken=adb9793829ddae60 kept, apiKey=abc masked", "PublicKeyToken=adb9793829ddae60 kept, apiKey=[secret] masked")]
    public void Emails_long_digit_runs_jwts_and_password_pairs_are_masked(string value, string expected)
    {
        TelemetryRedactor.Redact(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("trace 4bf92f3577b34da61234567890ce4736 ended")]
    [InlineData("span 00f067aa0ba902b7")]
    [InlineData("traceparent 00-4bf92f3577b34da61234567890ce4736-00f067aa0ba902b7-01")]
    [InlineData("company 3f2504e0-4f89-11d3-9a0c-030512345678")]
    [InlineData("answered 404 after 3 attempts")]
    [InlineData("on 2026-09-30 at 12:34:56.1234567")]
    [InlineData("قيمة ٤٠٤ قصيرة")]
    [InlineData("")]
    public void Trace_ids_guids_and_short_numbers_are_kept(string value)
    {
        TelemetryRedactor.Redact(value).ShouldBeSameAs(value, "nothing to mask: the very same string comes back");
    }

    [Theory]
    [InlineData("السجل التجاري ١٠١٠١٢٣٤٥٦ مسجل", "السجل التجاري [digits] مسجل")]
    [InlineData("الجوال ۰۵۵۱۲۳۴۵۶۷", "الجوال [digits]")]
    [InlineData("mixed ١٠١٠123456 run", "mixed [digits] run")]
    public void Arabic_indic_digit_runs_are_masked_too(string value, string expected)
    {
        TelemetryRedactor.Redact(value).ShouldBe(expected);
    }

    [Fact]
    public void A_number_glued_to_letters_that_are_not_hexadecimal_is_masked()
    {
        TelemetryRedactor.Redact("cr1010123456").ShouldBe("cr[digits]");
        TelemetryRedactor.Redact("vat:310123456789003").ShouldBe("vat:[digits]");
    }
}
