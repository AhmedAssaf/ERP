using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Platform.Modules.Operations.Contracts;
using Platform.Shared.Telemetry;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Platform.UnitTests.Shared;

/// <summary>
/// W-10, plan task 3 (spec O-10, O-11, section 5.3): the host's Serilog pipeline masks personal and secret values in
/// properties, destructured objects, literal library text and exceptions before any sink sees the event, refuses request
/// types, and names the component of every record.
/// </summary>
public sealed partial class TelemetryLogRedactionTests
{
    private const string Email = "ahmad@example.sa";
    private const string Phone = "0551234567";

    [Fact]
    public void An_object_logged_with_destructuring_has_its_strings_masked_and_request_types_refused()
    {
        var sink = new ListSink();
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?email={Email}");
        context.Request.Headers["X-Probe"] = Email;
        var form = new FormCollection(new Dictionary<string, StringValues> { ["Input.ContactEmail"] = Email });
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", Email)], "Test"));
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(Email));
        var contact = new Contact(Email, [Phone, "short"], new Dictionary<string, string> { [Email] = "key" }, new Inner($"Password=abc;Host={Email}"));

        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]))
        {
            logger.Information(
                "Probe {@Contact} {@Context} {@Request} {@Headers} {@Form} {@Principal} {@Body}",
                contact, context, context.Request, context.Request.Headers, form, principal, body);
        }

        var logEvent = sink.Events.ShouldHaveSingleItem();
        var structure = logEvent.Properties["Contact"].ShouldBeOfType<StructureValue>();
        Member(structure, "Address").ShouldBe(Scalar("[email]"));
        var phones = Member(structure, "Phones").ShouldBeOfType<SequenceValue>();
        phones.Elements.ShouldBe([Scalar("[digits]"), Scalar("short")]);
        var byEmail = Member(structure, "ByEmail").ShouldBeOfType<DictionaryValue>();
        byEmail.Elements.Keys.ShouldBe([new ScalarValue("[email]")]);
        var inner = Member(structure, "Inner").ShouldBeOfType<StructureValue>();
        // W-10 final fix wave: "=" is an RFC 5322 local-part character the platform accepts (EmailAddresses), so "Host=" is
        // taken into the marker with the address.
        Member(inner, "Settings").ShouldBe(Scalar("Password=[secret];[email]"));

        logEvent.Properties["Context"].ShouldBe(Scalar(typeof(DefaultHttpContext).FullName!));
        logEvent.Properties["Request"].ShouldBe(Scalar(context.Request.GetType().FullName!));
        logEvent.Properties["Headers"].ShouldBe(Scalar(context.Request.Headers.GetType().FullName!));
        logEvent.Properties["Form"].ShouldBe(Scalar(typeof(FormCollection).FullName!));
        logEvent.Properties["Principal"].ShouldBe(Scalar(typeof(ClaimsPrincipal).FullName!));
        logEvent.Properties["Body"].ShouldBe(Scalar(typeof(MemoryStream).FullName!));
        var rendered = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        rendered.ShouldNotContain("ahmad");
        rendered.ShouldNotContain(Phone);
        rendered.ShouldNotContain("abc");
    }

    [Fact]
    public void Destructuring_is_capped_in_depth_string_length_and_collection_count()
    {
        var sink = new ListSink();

        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]))
        {
            logger.Information("Caps {@Long} {@Many}", new { Text = new string('x', 5000) }, Enumerable.Range(0, 100).ToArray());
        }

        var logEvent = sink.Events.ShouldHaveSingleItem();
        var text = (string)((ScalarValue)Member(logEvent.Properties["Long"].ShouldBeOfType<StructureValue>(), "Text")).Value!;
        text.Length.ShouldBeLessThanOrEqualTo(4096);
        logEvent.Properties["Many"].ShouldBeOfType<SequenceValue>().Elements.Count.ShouldBe(32);
    }

    /// <summary>A value is masked before it is cut to 4096 characters, so an address across the cut never leaves in part.</summary>
    [Fact]
    public void An_email_across_the_length_cap_is_masked_before_the_value_is_cut()
    {
        var sink = new ListSink();
        var value = new string('x', 4090) + " " + Email + " tail";

        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]))
        {
            logger.Information("Long {Plain} {@Wrapped}", value, new { Text = value });
        }

        var logEvent = sink.Events.ShouldHaveSingleItem();
        var plain = (string)((ScalarValue)logEvent.Properties["Plain"]).Value!;
        var wrapped = (string)((ScalarValue)Member(logEvent.Properties["Wrapped"].ShouldBeOfType<StructureValue>(), "Text")).Value!;
        foreach (var text in new[] { plain, wrapped })
        {
            text.Length.ShouldBe(4096);
            text.ShouldNotContain("ahma");
            text.ShouldStartWith(new string('x', 4090) + " [ema", customMessage: "the marker, not the address, is what the cut shortens");
        }
    }

    [Fact]
    public void A_literal_library_message_is_masked_before_export()
    {
        var sink = new ListSink();

        using (var provider = new SerilogLoggerProvider(TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]), dispose: true))
        {
            var library = provider.CreateLogger("Some.Library.Mailer");
            Library.LiteralWithEmail(library);
            Library.LiteralAroundAToken(library, 3);
        }

        sink.Events.Count.ShouldBe(2);
        var literal = sink.Events[0];
        literal.MessageTemplate.Text.ShouldBe("Sending the invitation to [email] over SMTP.");
        literal.RenderMessage(CultureInfo.InvariantCulture).ShouldBe("Sending the invitation to [email] over SMTP.");
        var withToken = sink.Events[1];
        withToken.RenderMessage(CultureInfo.InvariantCulture).ShouldBe("Retry 3 for [email] failed.");
        withToken.MessageTemplate.Text.ShouldNotContain(Email);
        foreach (var logEvent in sink.Events)
        {
            logEvent.Properties.Values.ShouldAllBe(value => !value.ToString().Contains("ahmad", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void An_exception_leaves_as_its_type_masked_message_and_masked_stack_never_as_the_raw_exception()
    {
        var sink = new ListSink();
        using var source = new ActivitySource("Platform.UnitTests.Redaction");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        Exception thrown;
        try
        {
            throw new InvalidOperationException($"No user {Email} with phone {Phone}.");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        ActivityTraceId traceId;
        ActivitySpanId spanId;
        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Worker, [sink]))
        using (var activity = source.StartActivity("probe").ShouldNotBeNull())
        {
            traceId = activity.TraceId;
            spanId = activity.SpanId;
            logger.Error(thrown, "Lookup failed {Marker}", "m1");
        }

        var logEvent = sink.Events.ShouldHaveSingleItem();
        logEvent.Exception.ShouldBeNull("the raw exception, and with it its message, never reaches a sink");
        logEvent.Properties["exception.type"].ShouldBe(Scalar("System.InvalidOperationException"));
        logEvent.Properties["exception.message"].ShouldBe(Scalar("No user [email] with phone [digits]."));
        var stack = (string)((ScalarValue)logEvent.Properties["exception.stacktrace"]).Value!;
        stack.ShouldContain(nameof(An_exception_leaves_as_its_type_masked_message_and_masked_stack_never_as_the_raw_exception));
        stack.ShouldNotContain("ahmad");
        stack.ShouldNotContain(Phone);
        logEvent.TraceId.ShouldBe(traceId);
        logEvent.SpanId.ShouldBe(spanId);
        logEvent.Level.ShouldBe(LogEventLevel.Error);
        logEvent.Properties["Marker"].ShouldBe(Scalar("m1"));
    }

    [Fact]
    public void An_exception_logged_outside_any_span_still_has_no_trace_id_after_the_copy()
    {
        var sink = new ListSink();
        Activity.Current = null;

        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Worker, [sink]))
        {
            logger.Error(new InvalidOperationException("outside"), "Outside any span");
        }

        var logEvent = sink.Events.ShouldHaveSingleItem();
        logEvent.Exception.ShouldBeNull();
        logEvent.TraceId.ShouldBeNull();
        logEvent.SpanId.ShouldBeNull();
    }

    /// <summary>
    /// W-10 final fix wave (A.3; also the deferred task 3 and task 4 gaps): a property, a dictionary key or a structure member
    /// named as a credential or a connection string has its whole value replaced, whatever scheme or shape it holds (Digest,
    /// Token, unpadded Basic, a cookie, a number) and also when a header collection is logged without <c>@</c>.
    /// </summary>
    [Fact]
    public void A_value_under_a_secret_key_name_is_replaced_whole()
    {
        var sink = new ListSink();
        var headers = new Dictionary<string, string[]>
        {
            ["Authorization"] = ["Token opaque-header-value"],
            ["Cookie"] = [".AspNetCore.Cookies=CfDJ8opaque"],
            ["Accept"] = ["text/html"],
        };

        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]))
        {
            logger.Information(
                "Probe {Authorization} {Set_Cookie} {ConnectionString} {ApiKey} {Headers} {@Login}",
                "Digest username=\"monitor\", response=\"6629fae4\"",
                ".AspNetCore.Cookies=CfDJ8opaque; path=/",
                "Host=postgres;Username=erp_app;Database=erp",
                731_245,
                headers,
                new { User = "kept", Password = "opaque-password", Token = OpaqueTokens });
        }

        var logEvent = sink.Events.ShouldHaveSingleItem();
        logEvent.Properties["Authorization"].ShouldBe(Scalar("[secret]"));
        logEvent.Properties["Set_Cookie"].ShouldBe(Scalar("[secret]"));
        logEvent.Properties["ConnectionString"].ShouldBe(Scalar("[secret]"));
        logEvent.Properties["ApiKey"].ShouldBe(Scalar("[secret]"));
        var byName = logEvent.Properties["Headers"].ShouldBeOfType<DictionaryValue>().Elements
            .ToDictionary(e => (string)e.Key.Value!, e => e.Value.ToString());
        byName["Authorization"].ShouldBe("\"[secret]\"");
        byName["Cookie"].ShouldBe("\"[secret]\"");
        byName["Accept"].ShouldBe("[\"text/html\"]");
        var login = logEvent.Properties["Login"].ShouldBeOfType<StructureValue>();
        Member(login, "User").ShouldBe(Scalar("kept"));
        Member(login, "Password").ShouldBe(Scalar("[secret]"));
        Member(login, "Token").ShouldBe(Scalar("[secret]"));
        var rendered = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        foreach (var leaked in new[] { "6629fae4", "CfDJ8", "erp_app", "731245", "opaque" })
        {
            rendered.ShouldNotContain(leaked);
        }
    }

    /// <summary>
    /// W-10 final fix wave (E): a trace, span or parent id under its own name is never taken for a long number, even when
    /// every hexadecimal digit of it is a decimal one; the same digits under another name are masked.
    /// </summary>
    [Fact]
    public void Trace_span_and_parent_ids_under_their_own_names_are_kept_even_when_all_decimal()
    {
        var sink = new ListSink();

        using (var logger = TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]))
        {
            logger.Information(
                "Ids {TraceId} {SpanId} {ParentId} {Other}",
                "12345678901234567890123456789012", "1234567890123456", "0000000000000000", "1234567890123456");
        }

        var logEvent = sink.Events.ShouldHaveSingleItem();
        logEvent.Properties["TraceId"].ShouldBe(Scalar("12345678901234567890123456789012"));
        logEvent.Properties["SpanId"].ShouldBe(Scalar("1234567890123456"));
        logEvent.Properties["ParentId"].ShouldBe(Scalar("0000000000000000"));
        logEvent.Properties["Other"].ShouldBe(Scalar("[digits]"));
    }

    /// <summary>
    /// W-10 final fix wave (H.3): a Microsoft.Extensions.Logging scope becomes event properties before the last enricher runs,
    /// so a scope value holding an address is masked like any other property (the order is Serilog's own; this pins it).
    /// </summary>
    [Fact]
    public void A_log_scope_value_holding_an_email_is_masked()
    {
        var sink = new ListSink();

        using (var provider = new SerilogLoggerProvider(TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]), dispose: true))
        {
            var library = provider.CreateLogger("Some.Library.Mailer");
            using (library.BeginScope(new Dictionary<string, object?> { ["Contact"] = Email }))
            using (library.BeginScope("Inviting {Address}", Email))
            {
                Library.Plain(library);
            }
        }

        var logEvent = sink.Events.ShouldHaveSingleItem();
        logEvent.Properties["Contact"].ShouldBe(Scalar("[email]"));
        logEvent.Properties["Address"].ShouldBe(Scalar("[email]"));
        logEvent.Properties.Values.ShouldAllBe(value => !value.ToString().Contains("ahmad", StringComparison.Ordinal));
    }

    [Fact]
    public void A_query_string_property_is_blanked_in_the_property_and_the_rendered_message()
    {
        var sink = new ListSink();

        using (var provider = new SerilogLoggerProvider(TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]), dispose: true))
        {
            var hosting = provider.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
            Library.RequestStarting(hosting, "/signin-oidc", "?code=opaquecode&state=opaquestate");
        }

        var logEvent = sink.Events.ShouldHaveSingleItem();
        logEvent.Properties[RedactingEnricher.QueryString].ShouldBe(Scalar(string.Empty));
        logEvent.Properties["Path"].ShouldBe(Scalar("/signin-oidc"));
        var rendered = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        rendered.ShouldContain("/signin-oidc");
        rendered.ShouldNotContain("opaque");
        rendered.ShouldNotContain("?");
    }

    [Fact]
    public void Every_record_names_its_component()
    {
        var sink = new ListSink();

        using (var provider = new SerilogLoggerProvider(TelemetryModule.CreateLogger(TelemetryNames.Services.Web, [sink]), dispose: true))
        {
            Library.Plain(provider.CreateLogger("Platform.Modules.Vendors.Documents.VendorDocuments"));
            Library.Plain(provider.CreateLogger("Npgsql.Command"));
        }

        sink.Events.Select(e => e.Properties[TelemetryNames.Attributes.Component]).ShouldBe([Scalar("Vendors"), Scalar(HealthComponents.PostgreSql)]);
    }

    [Theory]
    [InlineData("Platform.Modules.Vendors.Documents.VendorDocuments", "Vendors")]
    [InlineData("Platform.Modules.Identity.Keycloak.KeycloakAdminClient", "Identity")]
    [InlineData("Platform.Modules.Operations", "Operations")]
    [InlineData("Npgsql.Command", HealthComponents.PostgreSql)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", HealthComponents.PostgreSql)]
    [InlineData("Amazon.S3.AmazonS3Client", HealthComponents.ObjectStorage)]
    [InlineData("MailKit.Net.Smtp.SmtpClient", HealthComponents.Email)]
    [InlineData("Hangfire.Server.Worker", "Jobs")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", HealthComponents.Web)]
    [InlineData("Platform.Web.Edge.KeyRing", HealthComponents.Web)]
    [InlineData("Platform.Shared.Jobs.JobTelemetryFilter", TelemetryNames.Services.Worker)]
    [InlineData("System.Net.Http.HttpClient.KeycloakAdminClient.LogicalHandler", TelemetryNames.Services.Worker)]
    [InlineData("Platform.ModulesX.Fake", TelemetryNames.Services.Worker)]
    [InlineData(null, TelemetryNames.Services.Worker)]
    public void The_component_comes_from_the_logger_category_by_the_fixed_table(string? category, string component)
    {
        ComponentEnricher.ComponentOf(category, TelemetryNames.Services.Worker).ShouldBe(component);
    }

    private static readonly string[] OpaqueTokens = ["opaque-token"];

    private static LogEventPropertyValue Member(StructureValue structure, string name) =>
        structure.Properties.Single(p => p.Name == name).Value;

    private static ScalarValue Scalar(string value) => new(value);

    private sealed record Contact(string Address, string[] Phones, Dictionary<string, string> ByEmail, Inner Inner);

    private sealed record Inner(string Settings);

    private sealed class ListSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public List<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }

    private static partial class Library
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Sending the invitation to ahmad@example.sa over SMTP.")]
        public static partial void LiteralWithEmail(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Retry {Attempt} for ahmad@example.sa failed.")]
        public static partial void LiteralAroundAToken(ILogger logger, int attempt);

        [LoggerMessage(Level = LogLevel.Information, Message = "Request starting GET {Path}{QueryString} - done")]
        public static partial void RequestStarting(ILogger logger, string path, string queryString);

        [LoggerMessage(Level = LogLevel.Error, Message = "Component probe.")]
        public static partial void Plain(ILogger logger);
    }
}
