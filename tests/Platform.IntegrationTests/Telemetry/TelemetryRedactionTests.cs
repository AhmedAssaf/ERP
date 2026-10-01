using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using Platform.IntegrationTests.Infrastructure;
using Platform.IntegrationTests.Vendors;
using Platform.IntegrationTests.Web;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Keycloak;
using Platform.Shared.Telemetry;
using Serilog.Events;
using ActivityKind = System.Diagnostics.ActivityKind;

namespace Platform.IntegrationTests.Telemetry;

/// <summary>
/// W-10, plan task 3 (spec O-10, O-11, section 5.3, N-10, PDPL): no personal or secret value leaves the web host in a log
/// record or a span. An exception leaves as its type, a masked message and a masked stack; a Keycloak lookup by email
/// leaves no email; a form value or query string is never captured; the Data Protection cap holds for the exporter's
/// provider too; every error record names its component.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class TelemetryRedactionTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_logged_exception_leaves_with_its_type_and_stack_and_a_masked_message()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Platform.Modules.Vendors.RedactionProbe");
        var marker = Marker();
        var person = Letters();
        var email = $"{person}@example.sa";

        Probes.Failed(logger, Thrown($"No vendor user {email}."), marker);

        var record = telemetry.Logs.Where(l => l.Properties.GetValueOrDefault("Marker") == marker).ShouldHaveSingleItem();
        record.Exception.ShouldBeNull("the raw exception, and with it its message, never reaches a sink");
        record.Level.ShouldBe(LogEventLevel.Error);
        record.Properties[RedactingEnricher.ExceptionType].ShouldBe("System.InvalidOperationException");
        record.Properties[RedactingEnricher.ExceptionMessage].ShouldBe("No vendor user [email].");
        var stack = record.Properties[RedactingEnricher.ExceptionStackTrace].ShouldNotBeNull();
        stack.ShouldNotBeEmpty();
        stack.ShouldContain(nameof(Thrown));
        ShouldNotCarry(record, person);
    }

    /// <summary>
    /// The instrumentations record no exception event (an event cannot be masked, spec 7.1): the server span of a request that
    /// threw is kept, with status Error and <c>exception.type</c>, and the masked message is on the log record of the same trace.
    /// </summary>
    [Fact]
    public async Task An_unhandled_exception_gives_an_error_span_with_its_type_and_no_address_and_a_masked_log_record()
    {
        var person = Letters();
        var telemetry = new CapturedTelemetry();
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            telemetry.AddTo(services);
            services.AddSingleton<IStartupFilter>(new ThrowingEndpoint($"No vendor user {person}@example.sa."));
        }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri(ThrowingEndpoint.Path, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem("the span of the failed request is exported, not dropped");
        server.Recorded.ShouldBeTrue();
        server.Status.ShouldBe(ActivityStatusCode.Error);
        server.GetTagItem(TelemetryNames.Attributes.ExceptionType).ShouldBe("System.InvalidOperationException");
        server.Events.ShouldBeEmpty("no exception event is recorded");
        foreach (var span in telemetry.SpansOf(factory.Services))
        {
            ShouldNotCarry(span, person);
        }

        var error = telemetry.Logs.Where(l => l.TraceId == server.TraceId && l.Level == LogEventLevel.Error).ShouldHaveSingleItem();
        error.Properties[RedactingEnricher.ExceptionType].ShouldBe("System.InvalidOperationException");
        error.Properties[RedactingEnricher.ExceptionMessage].ShouldBe("No vendor user [email].");
        foreach (var log in telemetry.Logs)
        {
            ShouldNotCarry(log, person);
        }
    }

    [Fact]
    public async Task Neither_instrumentation_records_exception_events_and_both_name_the_exception_type()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);

        var server = factory.Services.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>().CurrentValue;
        var client = factory.Services.GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>().CurrentValue;

        server.RecordException.ShouldBeFalse();
        server.EnrichWithException.ShouldNotBeNull();
        server.EnrichWithHttpResponse.ShouldNotBeNull("the static asset filter keeps its slot");
        client.RecordException.ShouldBeFalse();
        client.EnrichWithException.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_keycloak_admin_lookup_by_email_leaves_no_email_in_any_span_or_log()
    {
        var person = Letters();
        var email = $"{person}@example.sa";
        await using var keycloak = await FakeHttpServer.StartAsync(KeycloakAdminDouble, Ct);
        var telemetry = new CapturedTelemetry();
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("KeycloakAdmin:BaseUrl", keycloak.BaseAddress);
            builder.UseSetting("KeycloakAdmin:ClientSecret", "unused-in-tests");
            builder.ConfigureTestServices(telemetry.AddTo);
        });
        _ = factory.Services;
        using var source = new ActivitySource("WaslaBid.Tests.Redaction");
        ActivityTraceId trace;

        using (var root = source.StartActivity("keycloak lookup").ShouldNotBeNull())
        {
            trace = root.TraceId;
            await using var scope = factory.Services.CreateAsyncScope();
            var user = await scope.ServiceProvider.GetRequiredService<KeycloakAdminClient>().FindUserByEmailAsync(email, Ct);
            user.ShouldNotBeNull().Id.ShouldBe("user-1");
        }

        var spans = telemetry.AllSpans.Where(s => s.TraceId == trace).ToList();
        var lookup = spans.Where(s => s.Kind == ActivityKind.Client && Equals(s.GetTagItem("url.path") ?? PathOf(s), "/admin/realms/waslabid/users")).ShouldHaveSingleItem();
        lookup.GetTagItem("url.full").ShouldBe($"{keycloak.BaseAddress}admin/realms/waslabid/users");
        lookup.GetTagItem("url.query").ShouldBeNull();
        spans.Count(s => s.Kind == ActivityKind.Client).ShouldBeGreaterThanOrEqualTo(2, "the token request and the lookup");
        foreach (var span in spans)
        {
            ShouldNotCarry(span, person);
        }

        telemetry.Logs.ShouldNotBeEmpty("the HttpClient writes its request records");
        foreach (var log in telemetry.Logs)
        {
            ShouldNotCarry(log, person);
        }
    }

    [Fact]
    public async Task No_request_body_form_value_or_query_string_reaches_a_log_or_span()
    {
        var subject = Guid.NewGuid().ToString();
        var applicant = new TestUser(subject, [], "en", Email: $"{subject}@applicant.test", EmailVerified: true);
        var queryMarker = Letters();
        var formMarker = Letters();
        var telemetry = new CapturedTelemetry();
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            telemetry.AddTo(services);
            services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => new FakeVendorAccounts()));
        }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });

        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/vendor/register/company?probe={queryMarker}").As(applicant), Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var input = VendorRegistrationInputTests.Valid() with { NameEn = $"Trading {formMarker}", Address = $"Riyadh {formMarker}" };
        using var posted = await VendorAccessTests.PostRegistrationAsync(client, applicant, await page.Content.ReadAsStringAsync(Ct), input);

        (await posted.Content.ReadAsStringAsync(Ct)).ShouldContain("data-vendor-registered", customMessage: "the form was taken and processed");
        var servers = await telemetry.WaitForServerSpansAsync(factory.Services, 2, Ct);
        servers.Count.ShouldBe(2);
        servers.ShouldAllBe(s => s.GetTagItem("url.query") == null);
        var spans = telemetry.SpansOf(factory.Services);
        spans.Count.ShouldBeGreaterThan(servers.Count, "the registration's database commands are traced below the request");
        foreach (var marker in new[] { queryMarker, formMarker })
        {
            foreach (var span in spans)
            {
                ShouldNotCarry(span, marker);
            }

            foreach (var log in telemetry.Logs)
            {
                ShouldNotCarry(log, marker);
            }
        }
    }

    /// <summary>
    /// W-24's cap on Data Protection (Information for every provider) also holds for Serilog's provider, the one that feeds the
    /// OTLP exporter, with every other category opened to Trace in Testing.
    /// </summary>
    [Fact]
    public async Task The_key_ring_cap_also_holds_for_the_telemetry_exporter()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            }));
            builder.ConfigureTestServices(telemetry.AddTo);
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });

        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("W-10.Redaction");
        protector.Unprotect(protector.Protect("probe")).ShouldBe("probe");
        using var response = await client.GetAsync(new Uri("/admin/staff", UriKind.Relative), Ct);

        telemetry.Logs.ShouldContain(
            l => l.Level < LogEventLevel.Information && l.Category != null && l.Category.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
                && !l.Category.StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.Ordinal),
            "Trace is open for the other framework categories");
        telemetry.Logs.ShouldNotContain(l => l.Level < LogEventLevel.Information && l.Category != null
            && l.Category.StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_error_record_carries_a_component()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        var loggers = factory.Services.GetRequiredService<ILoggerFactory>();
        var marker = Marker();

        Probes.Error(loggers.CreateLogger("Platform.Modules.Vendors.RedactionProbe"), marker);
        Probes.Error(loggers.CreateLogger("Npgsql.RedactionProbe"), marker);

        var records = telemetry.Logs.Where(l => l.Properties.GetValueOrDefault("Marker") == marker).ToList();
        records.Count.ShouldBe(2);
        records.ShouldAllBe(r => r.Level == LogEventLevel.Error);
        records.Single(r => r.Category == "Platform.Modules.Vendors.RedactionProbe").Properties[TelemetryNames.Attributes.Component].ShouldBe("Vendors");
        records.Single(r => r.Category == "Npgsql.RedactionProbe").Properties[TelemetryNames.Attributes.Component].ShouldBe("PostgreSQL");
    }

    private WebApplicationFactory<Program> Factory(CapturedTelemetry telemetry) =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(telemetry.AddTo));

    /// <summary>The Keycloak token endpoint and the users search, answering one user with the searched email.</summary>
    private static async Task KeycloakAdminDouble(HttpContext context)
    {
        context.Response.ContentType = "application/json";
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
        {
            await context.Response.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["access_token"] = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJhZG1pbiJ9.",
                ["expires_in"] = 300,
            }), Ct);
            return;
        }

        if (path.EndsWith("/users", StringComparison.Ordinal))
        {
            await context.Response.WriteAsync(JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, object?> { ["id"] = "user-1", ["email"] = context.Request.Query["email"].ToString(), ["enabled"] = true },
            }), Ct);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static string? PathOf(Activity span) =>
        span.GetTagItem("url.full") is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : null;

    private static void ShouldNotCarry(Activity span, string value)
    {
        var where = $"span {span.DisplayName} ({span.Source.Name})";
        span.DisplayName.ShouldNotContain(value, Case.Insensitive, where);
        (span.StatusDescription ?? string.Empty).ShouldNotContain(value, Case.Insensitive, where);
        foreach (var tag in span.TagObjects)
        {
            Text(tag.Value).ShouldNotContain(value, Case.Insensitive, $"{where}, tag {tag.Key}");
        }

        foreach (var activityEvent in span.Events)
        {
            foreach (var tag in activityEvent.Tags)
            {
                Text(tag.Value).ShouldNotContain(value, Case.Insensitive, $"{where}, event {activityEvent.Name}, tag {tag.Key}");
            }
        }
    }

    private static void ShouldNotCarry(CapturedLog log, string value)
    {
        var where = $"log {log.Category}: {log.Template}";
        log.Message.ShouldNotContain(value, Case.Insensitive, where);
        log.Template.ShouldNotContain(value, Case.Insensitive, where);
        foreach (var (key, property) in log.Properties)
        {
            (property ?? string.Empty).ShouldNotContain(value, Case.Insensitive, $"{where}, property {key}");
        }
    }

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IEnumerable<string> many => string.Join(' ', many),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };

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

    private static string Marker() => Guid.NewGuid().ToString("N");

    /// <summary>A unique marker of letters only, which no masking rule touches: if it were found, it was captured as written.</summary>
    private static string Letters() => string.Concat(Guid.NewGuid().ToString("N").Select(c => char.IsAsciiDigit(c) ? (char)('g' + (c - '0')) : c));

    /// <summary>A test-only endpoint on tenant hosts that throws an exception with the given message.</summary>
    private sealed class ThrowingEndpoint(string message) : IStartupFilter
    {
        public const string Path = "/test/redaction/throw";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = app.Properties.TryGetValue("__EndpointRouteBuilder", out var value) && value is IEndpointRouteBuilder routeBuilder
                ? routeBuilder
                : throw new InvalidOperationException("The host did not expose its endpoint route builder.");
            endpoints.MapGet(Path, IResult () => throw new InvalidOperationException(message)).AllowAnonymous();
        };
    }

    private static partial class Probes
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Redaction probe failed {Marker}")]
        public static partial void Failed(ILogger logger, Exception exception, string marker);

        [LoggerMessage(Level = LogLevel.Error, Message = "Component probe {Marker}")]
        public static partial void Error(ILogger logger, string marker);
    }
}
