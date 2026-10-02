using Platform.Shared.Telemetry;
using Platform.Shared.Text;

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
    [InlineData("doc-3f2504e0-4f89-11d3-9a0c-030512345678 stored")]
    [InlineData("pair 3f2504e0-4f89-11d3-9a0c-030512345678-7d1f3c2e-4f89-11d3-9a0c-123456789012")]
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

    [Theory]
    [InlineData("ref 3f2504e0-4f89-11d3-9a0c-0305123456789 long", "ref 3f2504e0-4f89-11d3-9a0c-[digits] long")]
    [InlineData("x3f2504e0-4f89-11d3-9a0c-030512345678", "x3f2504e0-4f89-11d3-9a0c-[digits]")]
    public void A_dashed_run_that_is_not_a_guid_has_its_long_runs_masked(string value, string expected)
    {
        TelemetryRedactor.Redact(value).ShouldBe(expected);
    }

    /// <summary>W-10 task 4 ruling: an HTTP Basic credential (base64 of <c>user:password</c>) is masked like a Bearer one.</summary>
    [Theory]
    [InlineData("Basic bW9uaXRvcjpzM2NyZXQ=", "Basic [token]")]
    [InlineData("[\"Basic bW9uaXRvcjpzM2NyZXQ=\"]", "[\"Basic [token]\"]")]
    [InlineData("header basic   d2FzbGFiaWRfbW9uaXRvcjpwYXNzd29yZA== sent", "header basic   [token] sent")]
    [InlineData("BASIC dXNlcjpwYXNz, then", "BASIC [token], then")]
    public void Basic_credentials_are_masked(string value, string expected) =>
        TelemetryRedactor.Redact(value).ShouldBe(expected);

    [Theory]
    [InlineData("The basic plan has no AI review")]
    [InlineData("Basic auth is configured")]
    [InlineData("basic information about the tender")]
    [InlineData("basic")]
    public void Ordinary_words_after_basic_are_kept(string value) =>
        TelemetryRedactor.Redact(value).ShouldBeSameAs(value);

    /// <summary>W-10 task 4 ruling: the value of an <c>Authorization</c> header line is masked whatever its scheme.</summary>
    [Theory]
    [InlineData("Authorization: Digest username=\"monitor\", response=\"6629fae4\"", "Authorization: [token]")]
    [InlineData("authorization:opaque-api-credential-value", "authorization:[token]")]
    [InlineData("Proxy-Authorization: Negotiate YIIGqgYJKoZIhvcSAQICAQBuggaZ", "Proxy-Authorization: [token]")]
    [InlineData("Authorization: Basic not-a-credential\nnext line kept", "Authorization: [token]\nnext line kept")]
    [InlineData("Authorization: Basic bW9uaXRvcjpzM2NyZXQ=", "Authorization: Basic [token]")]
    public void The_value_of_an_authorization_header_line_is_masked(string value, string expected) =>
        TelemetryRedactor.Redact(value).ShouldBe(expected);

    [Theory]
    [InlineData("Authorization failed for policy PlatformAdmin")]
    [InlineData("Authorization:")]
    public void Authorization_without_a_header_value_is_kept(string value) =>
        TelemetryRedactor.Redact(value).ShouldBe(value);

    [Fact]
    public void A_number_glued_to_letters_that_are_not_hexadecimal_is_masked()
    {
        TelemetryRedactor.Redact("cr1010123456").ShouldBe("cr[digits]");
        TelemetryRedactor.Redact("vat:310123456789003").ShouldBe("vat:[digits]");
    }

    // W-10 final fix wave (pentest F-OBS-01 to F-OBS-07, final review): grouped digits, normalisation, Unicode classes,
    // colon secret pairs and key names. The pentest's own proofs are in Security/TelemetryRedactorBypassTests.

    [Theory]
    [InlineData("on 2026-10-02 12:34:56")]
    [InlineData("2026-10-02")]
    [InlineData("from 2026-10-02 12:34:56 +03:00 to 2026-10-03 00:00:00")]
    [InlineData("at 2026-10-02T12:34:56.1234567Z")]
    [InlineData("host 192.168.100.200 answered")]
    [InlineData("SDK 10.0.401 on 2026-10-02 12:34:56")]
    [InlineData("company 3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("job 12345678-1234-1234-1234-123456789012 done")]
    [InlineData("trace 4bf92f3577b34da6a3ce929d0e0e4736")]
    [InlineData("span 00f067aa0ba902b7")]
    [InlineData("took 00:00:01.2345678")]
    [InlineData("answered 404")]
    [InlineData("listening on http://127.0.0.1:5273 and https://acme.localhost:8443")]
    [InlineData("attempts 1 2 3 4 5 6 7 8 9 10")]
    public void Dates_times_addresses_versions_ids_and_short_numbers_are_kept(string value) =>
        TelemetryRedactor.Redact(value).ShouldBeSameAs(value);

    [Theory]
    [InlineData("Phone: 055 123 4567.", "Phone: [digits].")]
    [InlineData("Phone: 055-123-4567.", "Phone: [digits].")]
    [InlineData("call +1 555 123 4567", "call +[digits]")]
    [InlineData("IBAN SA03 8000 0000 6080 1016 7519 refused", "IBAN SA[digits] refused")]
    [InlineData("on 2026-10-02 call 055 123 4567", "on 2026-10-02 call [digits]")]
    public void Digit_groups_joined_by_one_space_or_hyphen_count_as_one_run(string value, string expected) =>
        TelemetryRedactor.Redact(value).ShouldBe(expected);

    // W-10 follow-ups (2026-10-02; spec 7.1, items 4 and 5): phones as people group them, references and dates kept. The
    // two tables are one decision: every row is a test, the rules in TelemetryRedactor were chosen to pass all of them.

    [Theory]
    [InlineData("call +966 5 5123 4567", "call +[digits]")]
    [InlineData("call +966 55 123 4567", "call +[digits]")]
    [InlineData("call 0 55 123 4567", "call [digits]")]
    [InlineData("call 055-123 4567", "call [digits]")]
    [InlineData("call 055 123-4567", "call [digits]")]
    [InlineData("call (011) 465 1234", "call ([digits]")]
    [InlineData("call +966 (11) 465 1234", "call +[digits]")]
    [InlineData("call 055.123.4567", "call [digits]")]
    [InlineData("call 055\t123\t4567", "call [digits]")]
    [InlineData("call 055  123  4567", "call [digits]")]
    [InlineData("call 055 - 123 - 4567 now", "call [digits] now")]
    [InlineData("phones 055.123.4567 and 055 123 4567", "phones [digits] and [digits]")]
    [InlineData("ip-like 966.551.234.567", "ip-like [digits]")]
    [InlineData("ip-like 1.012.345.678", "ip-like [digits]")]
    [InlineData("call +1.212.555.1234", "call +[digits]")]
    [InlineData("call +966.5.5123.4567", "call +[digits]")]
    [InlineData("call 055 123 4567.89", "call [digits].89")]
    [InlineData("step 1.055 123 4567", "step 1.[digits]")]
    [InlineData("on 2026-10-02 12:34:56 call 055-123 4567", "on 2026-10-02 12:34:56 call [digits]")]
    public void Phones_grouped_as_people_write_them_are_masked(string value, string expected) =>
        TelemetryRedactor.Redact(value).ShouldBe(expected);

    [Theory]
    [InlineData("host 192.168.100.200 answered")]
    [InlineData("hosts 10.0.0.1 10.0.0.2 10.20.30.40 192.168.100.200")]
    [InlineData("db 192.168.100.200:5432 up")]
    [InlineData("SDK 10.0.401")]
    [InlineData("Windows 10.0.26100.4061")]
    [InlineData("on 2026-10-02 12:34:56")]
    [InlineData("on 02-10-2026 12:34")]
    [InlineData("at 2026.10.02 12:34")]
    [InlineData("from 2026-10-02 10:00 to 2026-10-03 12")]
    [InlineData("days 2026-10-02\t2026-10-03\t2026-10-04")]
    [InlineData("took 00:00:01.2345678")]
    [InlineData("took 12:34:56.1234567 in 2026-10-02")]
    [InlineData("amount 12345678.90")]
    [InlineData("job 12345678-1234-1234-1234-123456789012 done")]
    [InlineData("trace 4bf92f3577b34da6a3ce929d0e0e4736 span 00f067aa0ba902b7")]
    [InlineData("listening on http://127.0.0.1:5273 and :8443")]
    [InlineData("answered 404 after 3 attempts")]
    [InlineData("attempts 1 2 3 4 5 6 7 8 9 10")]
    [InlineData("pages (1) (2) (3) (4) (5) (6) (7) (8) (9) (10)")]
    public void Addresses_versions_dates_times_ids_and_short_numbers_stay_unmasked_beside_the_phone_rules(string value) =>
        TelemetryRedactor.Redact(value).ShouldBeSameAs(value);

    /// <summary>
    /// The one conflict kept (item 4): a phone written digit by digit has the shape of a list of single digits
    /// (<c>attempts 1 2 3 4 5 6 7 8 9 10</c>), so two single-digit groups never join; nobody types a number so.
    /// </summary>
    [Fact]
    public void A_phone_written_digit_by_digit_is_kept_like_a_list_of_single_digits() =>
        TelemetryRedactor.Redact("call 0 5 5 1 2 3 4 5 6 7 8").ShouldBe("call 0 5 5 1 2 3 4 5 6 7 8");

    /// <summary>
    /// Fix round 1 (controller ruling, 2026-10-02): only the platform's own reference prefixes (<c>RFP</c>, <c>RFQ</c>,
    /// <c>PO</c>, <c>TND</c>, any case) followed by a year and one sequence number of up to six digits are kept; a date keeps a
    /// suffix of one or two digits only.
    /// </summary>
    [Theory]
    [InlineData("tender RFP-2026-000045 opened")]
    [InlineData("/tenders/rfp-2026-000045/offers")]
    [InlineData("quote RFQ-2026-12 sent")]
    [InlineData("order PO-2026-000123 issued")]
    [InlineData("Po-2026-1 issued")]
    [InlineData("tender TND-2026-014 closed")]
    [InlineData("tnd-2049-999999")]
    [InlineData("item W-10-2026-09-30 done")]
    [InlineData("export 2026-10-02-15 ready")]
    [InlineData("export 2026-10-02-7")]
    [InlineData("RFP-2026-000045 and RFP-2026-000046")]
    public void References_and_dates_with_a_short_suffix_stay_unmasked(string value) =>
        TelemetryRedactor.Redact(value).ShouldBeSameAs(value);

    [Theory]
    [InlineData("call 055-123-4567", "call [digits]")]
    [InlineData("IBAN SA03-8000-0000-6080-1016-7519 refused", "IBAN SA[digits] refused")]
    [InlineData("mobile-055-123-4567", "mobile-[digits]")]
    [InlineData("tel-1-212-555-1234", "tel-[digits]")]
    [InlineData("CR-1010-123456", "CR-[digits]")]
    [InlineData("ID-2123-456789", "ID-[digits]")]
    [InlineData("CR-2050-123456", "CR-[digits]")]
    [InlineData("acct-2026-0000-6080-1016-7519", "acct-[digits]")]
    [InlineData("RFP-2026-0551234567", "RFP-[digits]")]
    [InlineData("RFP-2026-000045 055 123 4567", "RFP-2026-000045 [digits]")]
    [InlineData("export 2026-10-02-055-123-4567", "export [digits]")]
    [InlineData("ID-2012-345678", "ID-[digits]")]
    [InlineData("Iqama-2012-345-678", "Iqama-[digits]")]
    [InlineData("upload CR-2030-123456.pdf", "upload CR-[digits].pdf")]
    [InlineData("x-2026-0551-234-567", "x-[digits]")]
    [InlineData("card-2031-4567-8901-2345", "card-[digits]")]
    [InlineData("RFP-2026-055-123-4567", "RFP-[digits]")]
    [InlineData("RFP-2026-05512345", "RFP-[digits]")]
    [InlineData("PO-2026-10-02-0001 issued", "PO-[digits] issued")]
    [InlineData("F-29-2026-1234 scored", "F-[digits] scored")]
    [InlineData("backup 2026-10-02-153045 kept", "backup [digits] kept")]
    [InlineData("export 2026-10-02-15-0551234", "export [digits]")]
    [InlineData("طلب-2026-000045", "طلب-[digits]")]
    public void Hyphen_grouped_phones_ibans_and_ids_are_still_masked_beside_the_reference_rules(string value, string expected) =>
        TelemetryRedactor.Redact(value).ShouldBe(expected);

    [Theory]
    [InlineData("تم‏ الإرسال إلى الشركة")]
    [InlineData("ﻻ presentation form, nothing to mask")]
    [InlineData("soft­hyphen kept")]
    public void A_value_that_is_not_ascii_with_nothing_to_mask_comes_back_as_the_same_instance(string value) =>
        TelemetryRedactor.Redact(value).ShouldBeSameAs(value);

    [Theory]
    [InlineData("ahmad​@example.sa", "[email]")]
    [InlineData("CR 1010­123456 ‏كتب", "CR [digits] كتب")]
    [InlineData("CR १०१०१२३४५६ (Devanagari)", "CR [digits] (Devanagari)")]
    [InlineData("phone ０５５ １２３ ４５６７", "phone [digits]")]
    public void A_masked_value_comes_back_normalised_without_format_characters(string value, string expected) =>
        TelemetryRedactor.Redact(value).ShouldBe(expected);

    [Fact]
    public void A_lone_surrogate_does_not_stop_the_masking()
    {
        TelemetryRedactor.Redact("\uD800 to ahmad@example.sa").ShouldEndWith("to [email]");
        const string nothing = "\uDC00 nothing";
        TelemetryRedactor.Redact(nothing).ShouldBeSameAs(nothing);
    }

    public static TheoryData<string> AcceptedAddresses()
    {
        var data = new TheoryData<string>
        {
            "ahmad@acme.sa",
            "Ahmad.Alharbi@Acme.COM.sa",
            "first.last+tenders@mail.acme-co.sa",
            "1@acme.sa",
            "a@1acme.sa",
            "a@acme.123",
            "x@a.b",
            "o'neil.and.sons@acme.sa",
            "very.long.local.part.with.many.dots@sub.domain.example.travel",
        };
        foreach (var c in "!#$%&'*+/=?^_`{|}~-")
        {
            data.Add($"a{c}b@acme.sa");
            data.Add($"{c}x@acme.sa");
            data.Add($"x.{c}@acme.sa");
        }

        return data;
    }

    /// <summary>
    /// Every local-part character <see cref="EmailAddresses"/> accepts (F-06 invitations, F-11 vendor contacts), at the start,
    /// in the middle and after a dot, and the domain shapes it accepts: nothing of an address is left before the marker.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptedAddresses))]
    public void Every_address_the_platform_accepts_is_masked_whole(string address)
    {
        EmailAddresses.Normalize(address).ShouldNotBeNull("the platform accepts this address");

        TelemetryRedactor.Redact($"invitation to {address} failed").ShouldBe("invitation to [email] failed");
        TelemetryRedactor.Redact($"<{address}>").ShouldBe("<[email]>");
    }

    [Theory]
    [InlineData("{\"password\":\"hunter2\",\"user\":\"u\"}", "{\"password\":\"[secret]\",\"user\":\"u\"}")]
    [InlineData("{\"client_secret\": \"two words\", \"user\": \"u\"}", "{\"client_secret\": \"[secret]\", \"user\": \"u\"}")]
    [InlineData("password: hunter2 next", "password: [secret] next")]
    [InlineData("apikey : hunter2, next", "apikey : [secret], next")]
    [InlineData("PASSWORD = hunter2 next", "PASSWORD=[secret] next")]
    public void A_secret_pair_written_with_a_colon_or_as_json_is_masked(string value, string expected) =>
        TelemetryRedactor.Redact(value).ShouldBe(expected);

    /// <summary>W-10 follow-up N2: an escaped quote or backslash inside a JSON secret value does not end the masked value.</summary>
    [Theory]
    [InlineData("{\"password\":\"ab\\\"cdSUFFIX\"}", "{\"password\":\"[secret]\"}")]
    [InlineData("{\"client_secret\": \"a\\\"b\\\"c\", \"user\": \"u\"}", "{\"client_secret\": \"[secret]\", \"user\": \"u\"}")]
    [InlineData("{\"pwd\":\"ends with a backslash\\\\\",\"user\":\"u\"}", "{\"pwd\":\"[secret]\",\"user\":\"u\"}")]
    [InlineData("{\"password\":\"\\\\\\\"hidden\\\\\"}", "{\"password\":\"[secret]\"}")]
    public void An_escaped_quote_or_backslash_in_a_json_secret_value_is_masked_with_it(string value, string expected) =>
        TelemetryRedactor.Redact(value).ShouldBe(expected);

    /// <summary>
    /// W-10 follow-up (PDPL): only a 16 or 32 character lower-case hexadecimal token is an id (span, trace, GUID "N"); a
    /// compact IBAN whose letters happen to be hexadecimal is not, and its digits are masked.
    /// </summary>
    [Theory]
    [InlineData("IBAN AE070331234567890123456 refused", "IBAN AE[digits] refused")]
    [InlineData("IBAN DE89370400440532013000 refused", "IBAN DE[digits] refused")]
    [InlineData("IBAN BE68539007547034 refused", "IBAN BE[digits] refused")]
    [InlineData("ibans ae070331234567890123456 and de89370400440532013000", "ibans ae[digits] and de[digits]")]
    public void A_compact_iban_whose_letters_are_hexadecimal_is_masked(string value, string expected) =>
        TelemetryRedactor.Redact(value).ShouldBe(expected);

    [Theory]
    [InlineData("span 00f067aa0ba902b7 and trace 4bf92f3577b34da61234567890ce4736")]
    [InlineData("span 1234567890abcdef")]
    [InlineData("guid n 3f2504e04f8911d39a0c030512345678")]
    [InlineData("guid 3F2504E0-4F89-11D3-9A0C-030512345678 upper")]
    [InlineData("traceparent 00-4bf92f3577b34da61234567890ce4736-00f067aa0ba902b7-01")]
    public void Span_ids_trace_ids_and_guids_are_still_kept(string value) =>
        TelemetryRedactor.Redact(value).ShouldBeSameAs(value);

    [Theory]
    [InlineData("Invalid password")]
    [InlineData("Secrets: 3 loaded")]
    [InlineData("password reset requested at 12:30")]
    public void A_secret_word_without_a_value_is_kept(string value) =>
        TelemetryRedactor.Redact(value).ShouldBeSameAs(value);

    [Theory]
    [InlineData("Authorization")]
    [InlineData("proxy-authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("password")]
    [InlineData("NewPassword")]
    [InlineData("Pwd")]
    [InlineData("passwd")]
    [InlineData("secret")]
    [InlineData("client_secret")]
    [InlineData("ClientSecret")]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("api-key")]
    [InlineData("X-Api-Key")]
    [InlineData("APIKEY")]
    [InlineData("connection-string")]
    [InlineData("ConnectionString")]
    [InlineData("db.client.connection.pool.name")]
    [InlineData("db.npgsql.data_source")]
    [InlineData("DB_NPGSQL_DATA_SOURCE")]
    public void A_secret_key_name_is_recognised_whatever_its_case_and_separators(string key) =>
        TelemetryRedactor.IsSecretKey(key).ShouldBeTrue();

    [Theory]
    [InlineData("TraceId")]
    [InlineData("SpanId")]
    [InlineData("waslabid.tenant.id")]
    [InlineData("url.path")]
    [InlineData("user_agent.original")]
    [InlineData("SourceContext")]
    [InlineData("ConnectionId")]
    [InlineData("token_type")]
    [InlineData("PasswordPolicy")]
    [InlineData("db.system.name")]
    [InlineData("")]
    public void An_ordinary_key_name_is_not_a_secret(string key) =>
        TelemetryRedactor.IsSecretKey(key).ShouldBeFalse();
}
