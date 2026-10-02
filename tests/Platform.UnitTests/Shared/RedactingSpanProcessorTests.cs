using System.Diagnostics;
using Platform.Shared.Telemetry;

namespace Platform.UnitTests.Shared;

/// <summary>
/// W-10, plan task 3 (spec O-10, O-11, section 7.1): before a span is exported, its query string and header tags are gone and
/// every string tag, the display name and the status description are masked. Span events are never exported (the OTLP
/// exporter's event limit is 0, <see cref="TelemetryModule.SpanEventCountLimit"/>; proved on the exporter's wire output in
/// the integration test <c>TelemetryDataSourceTests</c>), so an event no longer withholds a span; an exception event's type
/// is kept on the span.
/// </summary>
public sealed class RedactingSpanProcessorTests
{
    private const string Email = "ahmad@example.sa";

    private static readonly string[] AuthorizationHeader = ["Bearer abc"];
    private static readonly string[] CookieHeader = ["session=abc"];
    private static readonly string[] MaskedLabels = ["[email]", "kept"];

    [Fact]
    public void A_span_leaves_without_its_query_string_or_headers_and_with_its_strings_masked()
    {
        using var span = Recorded("GET");
        span.SetTag("url.full", "http://keycloak:8080/admin/realms/waslabid/users?email=ahmad%40example.sa&exact=true#top");
        span.SetTag("url.query", "email=ahmad%40example.sa&exact=true");
        span.SetTag("url.path", $"/vendor/{Email}");
        span.SetTag("http.request.header.authorization", AuthorizationHeader);
        span.SetTag("http.response.header.set-cookie", CookieHeader);
        span.SetTag("db.query.text", "SELECT 1 /* Password=abc; */");
        span.SetTag("labels", new[] { Email, "kept" });
        span.SetTag("http.response.status_code", 200);
        span.SetTag(TelemetryNames.Attributes.TenantId, "7d1f3c2e-4f89-11d3-9a0c-030512345678");
        span.SetStatus(ActivityStatusCode.Error, $"No user {Email}.");
        span.DisplayName = $"GET /vendor/{Email}";
        span.Stop();

        new RedactingSpanProcessor().OnEnd(span);

        span.GetTagItem("url.query").ShouldBeNull();
        span.GetTagItem("url.full").ShouldBe("http://keycloak:8080/admin/realms/waslabid/users");
        // W-10 final fix wave: "/" is an RFC 5322 local-part character the platform accepts (EmailAddresses), so the path
        // segments before the address are taken into the marker with it.
        span.GetTagItem("url.path").ShouldBe("[email]");
        span.GetTagItem("http.request.header.authorization").ShouldBeNull();
        span.GetTagItem("http.response.header.set-cookie").ShouldBeNull();
        span.GetTagItem("db.query.text").ShouldBe("SELECT 1 /* Password=[secret]; */");
        span.GetTagItem("labels").ShouldBe(MaskedLabels);
        span.GetTagItem("http.response.status_code").ShouldBe(200);
        span.GetTagItem(TelemetryNames.Attributes.TenantId).ShouldBe("7d1f3c2e-4f89-11d3-9a0c-030512345678");
        span.StatusDescription.ShouldBe("No user [email].");
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.DisplayName.ShouldBe("GET [email]");
        span.Recorded.ShouldBeTrue("a span whose values could all be masked is still exported");
    }

    [Fact]
    public void A_key_added_twice_is_masked_in_both_occurrences_and_the_order_is_kept()
    {
        using var span = Recorded("GET");
        span.AddTag("first", "kept");
        span.AddTag("probe", Email);
        span.AddTag("probe", $"second {Email}");
        span.AddTag("url.query", "a=1");
        span.AddTag("url.query", "b=2");
        span.Stop();

        new RedactingSpanProcessor().OnEnd(span);

        span.TagObjects.ShouldBe(
        [
            new KeyValuePair<string, object?>("first", "kept"),
            new KeyValuePair<string, object?>("probe", "[email]"),
            new KeyValuePair<string, object?>("probe", "second [email]"),
        ]);
    }

    /// <summary>
    /// W-10 follow-up, fix round 1: an event's tags cannot be masked, but no event is exported any more, so a span whose
    /// exception event holds a personal value is exported (it used to be withheld whole) and keeps the exception's type.
    /// </summary>
    [Fact]
    public void A_span_with_an_exception_event_that_holds_a_personal_value_is_exported_with_the_exception_type()
    {
        using var span = Recorded("SELECT");
        span.AddException(Thrown($"No user {Email} with CR 1010123456."));
        span.SetStatus(ActivityStatusCode.Error, $"No user {Email}.");
        span.Stop();

        new RedactingSpanProcessor().OnEnd(span);

        span.Recorded.ShouldBeTrue();
        span.IsAllDataRequested.ShouldBeTrue();
        span.GetTagItem(TelemetryNames.Attributes.ExceptionType).ShouldBe(typeof(InvalidOperationException).FullName);
        span.StatusDescription.ShouldBe("No user [email].");
    }

    [Fact]
    public void An_exception_type_already_on_the_span_is_kept()
    {
        using var span = Recorded("GET");
        span.SetTag(TelemetryNames.Attributes.ExceptionType, "System.Net.Http.HttpRequestException");
        span.AddException(Thrown("Connection refused."));
        span.Stop();

        new RedactingSpanProcessor().OnEnd(span);

        span.TagObjects.ShouldHaveSingleItem().ShouldBe(
            new KeyValuePair<string, object?>(TelemetryNames.Attributes.ExceptionType, "System.Net.Http.HttpRequestException"));
    }

    [Fact]
    public void A_span_with_nothing_to_mask_is_left_as_it_was()
    {
        using var span = Recorded("job");
        span.SetTag(TelemetryNames.Attributes.JobType, "HealthCheckJob.RunAsync");
        span.Stop();

        new RedactingSpanProcessor().OnEnd(span);

        span.TagObjects.ShouldHaveSingleItem().ShouldBe(new KeyValuePair<string, object?>(TelemetryNames.Attributes.JobType, "HealthCheckJob.RunAsync"));
        span.StatusDescription.ShouldBeNull();
        span.Recorded.ShouldBeTrue();
    }

    /// <summary>
    /// W-10 final fix wave (final review; spec O-11): the user agent is a request header value and never leaves; a tag under a
    /// secret key name (Npgsql's connection-string names, credentials) is dropped whatever its value holds.
    /// </summary>
    [Fact]
    public void A_span_loses_its_user_agent_and_every_tag_under_a_secret_key_name()
    {
        using var span = Recorded("SELECT");
        span.SetTag("user_agent.original", "Mozilla/5.0 (Windows NT 10.0) probe");
        span.SetTag("db.npgsql.data_source", "Host=postgres;Port=5432;Username=erp_app;Database=erp");
        span.SetTag("db.client.connection.pool.name", "Host=postgres;Username=erp_app");
        span.SetTag("client_secret", "opaque-value");
        span.SetTag("X-Api-Key", "opaque-value");
        span.SetTag("url.path", "/kept");
        span.Stop();

        new RedactingSpanProcessor().OnEnd(span);

        span.TagObjects.ShouldBe([new KeyValuePair<string, object?>("url.path", "/kept")]);
        span.Recorded.ShouldBeTrue();
    }

    /// <summary>An event tag under a secret key name no longer withholds the span: the event itself is never exported.</summary>
    [Fact]
    public void A_span_with_an_event_tag_under_a_secret_key_name_is_still_exported()
    {
        using var span = Recorded("POST");
        span.AddEvent(new ActivityEvent("sent", tags: new ActivityTagsCollection { ["authorization"] = "opaque-value" }));
        span.Stop();

        new RedactingSpanProcessor().OnEnd(span);

        span.Recorded.ShouldBeTrue();
    }

    /// <summary>
    /// A failed database command (source <c>Npgsql</c>, an exception event with a value that would be masked) is exported
    /// with its tags, its masked status and its exception type.
    /// </summary>
    [Fact]
    public void A_failed_database_span_is_exported_with_its_exception_type_and_masked_status()
    {
        using var source = new ActivitySource(TelemetryNames.Sources.Npgsql);
        using var listener = Listening(source);
        using var span = source.StartActivity("INSERT").ShouldNotBeNull();
        span.SetTag("db.query.text", "INSERT INTO t VALUES ($1)");
        span.AddEvent(new ActivityEvent("received-first-response"));
        span.AddException(Thrown($"duplicate key value: Key (email)=({Email}) already exists."));
        span.SetStatus(ActivityStatusCode.Error, $"Key (email)=({Email})");
        span.Stop();

        new RedactingSpanProcessor().OnEnd(span);

        span.Recorded.ShouldBeTrue();
        span.GetTagItem("db.query.text").ShouldBe("INSERT INTO t VALUES ($1)");
        span.GetTagItem(TelemetryNames.Attributes.ExceptionType).ShouldBe(typeof(InvalidOperationException).FullName);
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.StatusDescription.ShouldBe("Key (email)=([email])");
    }

    private static ActivityListener Listening(ActivitySource source)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => ReferenceEquals(s, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static Activity Recorded(string name)
    {
        var span = new Activity(name) { ActivityTraceFlags = ActivityTraceFlags.Recorded };
        return span.Start();
    }

    private static InvalidOperationException Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }
}
