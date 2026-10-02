using Platform.Shared.Telemetry;
using Platform.Shared.Text;

namespace Platform.UnitTests.Security;

/// <summary>
/// Pentest proof for the W-10 observability pipeline (spec docs/superpowers/specs/2026-09-30-observability-design.md, O-10;
/// ADR-0014). <see cref="TelemetryRedactor"/> is the layer-2 backstop for framework and library text the hosts do not write:
/// span <c>url.path</c>, <c>user_agent.original</c>, <c>db.query.text</c>, library exception messages, and any attacker-
/// controlled value that reaches a span tag or log property. These cases were reproduced end to end against the running
/// stack: each input below was sent on a request header or URL and the SAME unmasked value was then read back out of the
/// Elasticsearch document (traces-generic.otel-default: <c>attributes.url.path</c>, <c>attributes.user_agent.original</c>).
///
/// Every test asserts the SECURE outcome (the sensitive token is masked). They are RED on w-10-pipeline by design: they are
/// the proof that the redactor misses these shapes. The fix belongs in production code (TelemetryRedactor), not here — do not
/// weaken or delete these assertions to make them pass; make the redactor mask the value.
///
/// Classification: Medium / Low, defence-in-depth. Layer 1 (LogTemplateTests) keeps our own templates free of these values,
/// so the exposure is limited to framework/library text and user-controlled fields that flow into it. The e2e step-5 check
/// only looks for ASCII emails and contiguous digit runs, so it passes while these shapes leak.
/// </summary>
public sealed class TelemetryRedactorBypassTests
{
    /// <summary>
    /// F-OBS-01 (Medium): a secret written <c>password: value</c> or <c>"password":"value"</c> (colon, the form config dumps,
    /// JSON and many libraries use) is not masked; only <c>password=value</c> is. Reproduced: a request User-Agent carrying
    /// <c>password: hunter2xyz {"password":"hunter2xyz"}</c> was stored verbatim in <c>attributes.user_agent.original</c>.
    /// </summary>
    [Theory]
    [InlineData("password: hunter2value")]
    [InlineData("pwd: hunter2value")]
    [InlineData("secret: hunter2value")]
    [InlineData("\"password\":\"hunter2value\"")]
    [InlineData("\"client_secret\": \"hunter2value\"")]
    [InlineData("apikey : hunter2value")]
    public void A_colon_separated_secret_pair_is_masked(string value)
    {
        TelemetryRedactor.Redact(value).ShouldNotContain("hunter2value", Case.Sensitive,
            $"a colon-separated secret survived redaction: {value}");
    }

    /// <summary>
    /// F-OBS-02 (Medium): a phone or IBAN written with the spaces people actually type is not masked, because the redactor
    /// only catches a single contiguous run of ten or more digits. Reproduced: <c>url.path</c> segments
    /// <c>+966 55 123 4567</c> and <c>SA03 8000 0000 6080 1016 7519</c> were stored unmasked in traces.
    /// </summary>
    [Theory]
    [InlineData("+966 55 123 4567")]
    [InlineData("0551 234 567 890")]
    [InlineData("SA03 8000 0000 6080 1016 7519")]
    [InlineData("SA03-8000-0000-6080-1016-7519")]
    public void A_spaced_or_grouped_phone_or_iban_is_masked(string value)
    {
        TelemetryRedactor.Redact(value).ShouldContain(TelemetryRedactor.DigitsMarker,
            customMessage: $"a grouped phone/IBAN kept its digits: {value}");
    }

    /// <summary>
    /// F-OBS-07 (Medium): the platform's own email rule (<see cref="EmailAddresses"/>, used for staff invitations F-06 and
    /// vendor contacts F-11) accepts the RFC 5322 local-part characters <c>= ' ! # { }</c>; the redactor's email pattern knows
    /// only <c>. _ % + -</c>, so the local part up to such a character is left in clear before the <c>[email]</c> marker
    /// (<c>ahmad.alharbi=sales@acme.sa</c> -> <c>ahmad.alharbi=[email]</c>). A real stored address therefore partly leaks.
    /// </summary>
    [Theory]
    [InlineData("ahmad.alharbi=sales@acme.sa")]
    [InlineData("o'neil@acme.sa")]
    [InlineData("ahmad!@acme.sa")]
    [InlineData("first{x}@acme.sa")]
    [InlineData("ahmad#tenders@acme.sa")]
    public void An_address_the_platform_accepts_is_masked_whole(string address)
    {
        EmailAddresses.Normalize(address).ShouldNotBeNull("the platform stores this address");

        TelemetryRedactor.Redact($"invitation to {address} failed").ShouldBe("invitation to [email] failed");
    }

    /// <summary>
    /// F-OBS-03 (Medium): an email whose domain is an internationalised (non-ASCII) name is not masked. Reproduced: the
    /// URL path <c>/.../ahmad@شركة.السعودية</c> was stored unmasked in <c>attributes.url.path</c>.
    /// </summary>
    [Theory]
    [InlineData("ahmad@شركة.السعودية")]
    [InlineData("ahmad@例え.jp")]
    public void An_internationalised_domain_email_is_masked(string value)
    {
        TelemetryRedactor.Redact(value).ShouldBe(TelemetryRedactor.EmailMarker,
            $"an IDN email survived redaction: {value}");
    }

    /// <summary>
    /// F-OBS-04 (Low): a full-width at sign (U+FF20) makes an email the redactor does not recognise.
    /// </summary>
    [Fact]
    public void A_full_width_at_sign_email_is_masked() =>
        TelemetryRedactor.Redact("ahmad＠example.sa").ShouldBe(TelemetryRedactor.EmailMarker);

    /// <summary>
    /// F-OBS-05 (Low): full-width digits (U+FF10-FF19) are not treated as digits, so a CR/phone typed on a full-width
    /// keyboard is not masked, unlike the Arabic-Indic digits the redactor does handle.
    /// </summary>
    [Fact]
    public void A_full_width_digit_run_is_masked() =>
        TelemetryRedactor.Redact("CR １０１０１２３４５６ registered")
            .ShouldBe($"CR {TelemetryRedactor.DigitsMarker} registered");

    /// <summary>
    /// F-OBS-06 (Low): a zero-width joiner (U+200D) or bidi isolate (U+2066) placed inside a long digit run splits it into
    /// two sub-ten runs, so neither half is masked and the number leaks. An attacker who controls a free-text field can
    /// hide a national id or CR number from the backstop this way.
    /// </summary>
    [Theory]
    [InlineData("1010‍123456")]
    [InlineData("1010⁦123456")]
    public void A_zero_width_split_digit_run_is_masked(string value)
    {
        TelemetryRedactor.Redact(value).ShouldContain(TelemetryRedactor.DigitsMarker,
            customMessage: $"a zero-width-split digit run leaked: {value}");
    }
}
