using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Telemetry;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Telemetry;

/// <summary>
/// W-10, plan task 2 (spec O-7, O-8, O-9, section 5): every log record of a request or circuit event carries its context
/// (tenant id and slug, the user's <c>sub</c>, the vendor company) and the trace id; every response names its trace id in
/// <c>X-Correlation-Id</c>; a <c>traceparent</c> from a client never becomes the request's trace.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class TelemetryContextTests(DatabaseFixture db)
{
    private const string CorrelationHeader = "X-Correlation-Id";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_log_written_during_a_tenant_request_carries_the_tenant_id_slug_and_trace_id()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        using var client = ClientFor(factory, "acme.localhost");
        var marker = Marker();

        using var response = await client.GetAsync(new Uri($"{ProbeEndpoints.LogPath}?marker={marker}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem();
        var record = ProbeRecord(telemetry, marker);
        record.Properties[TelemetryNames.Attributes.TenantId].ShouldBe(TestTenants.Acme.TenantId.ToString());
        record.Properties[TelemetryNames.Attributes.TenantSlug].ShouldBe(TestTenants.Acme.Slug);
        record.TraceId.ShouldBe(server.TraceId);
        server.GetTagItem(TelemetryNames.Attributes.TenantId).ShouldBe(TestTenants.Acme.TenantId.ToString());
        server.GetTagItem(TelemetryNames.Attributes.TenantSlug).ShouldBe(TestTenants.Acme.Slug);
    }

    [Fact]
    public async Task A_log_written_after_sign_in_carries_the_user_id_and_never_the_email()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        using var client = ClientFor(factory, "acme.localhost");
        var marker = Marker();
        var email = $"telemetry-{marker}@acme.test";
        var user = new TestUser($"user-{marker}", ["acme"], "en", Email: email, EmailVerified: true);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{ProbeEndpoints.LogPath}?marker={marker}").As(user), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem();
        var record = ProbeRecord(telemetry, marker);
        record.Properties[TelemetryNames.Attributes.UserId].ShouldBe(user.Subject);
        record.Properties[TelemetryNames.Attributes.TenantId].ShouldBe(TestTenants.Acme.TenantId.ToString(), "the user's scope nests inside the tenant's");
        server.GetTagItem(TelemetryNames.Attributes.UserId).ShouldBe(user.Subject);
        var request = telemetry.Logs.Where(l => l.TraceId == server.TraceId).ToList();
        request.ShouldContain(record);
        foreach (var log in request)
        {
            log.Message.ShouldNotContain(email, Case.Insensitive);
            log.Properties.Values.ShouldAllBe(value => value == null || !value.Contains(email, StringComparison.OrdinalIgnoreCase));
        }

        server.TagObjects.ShouldAllBe(tag => tag.Value == null || !tag.Value.ToString()!.Contains(email, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_vendor_request_log_carries_the_vendor_company_id()
    {
        var subject = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, subject, VendorRows.NewCrNumber(), "Telemetry Trading", Ct);
        var vendor = new TestUser(subject, ["acme"], "en", RealmRoles: [IdentityClaims.VendorRealmRole], Email: $"{subject}@vendor.test", EmailVerified: true);
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        using var client = ClientFor(factory, "acme.localhost");
        var marker = Marker();

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{ProbeEndpoints.VendorLogPath}?marker={marker}").As(vendor), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem();
        var record = ProbeRecord(telemetry, marker);
        record.Properties[TelemetryNames.Attributes.VendorCompanyId].ShouldBe(companyId.ToString());
        record.Properties[TelemetryNames.Attributes.UserId].ShouldBe(subject);
        record.Properties[TelemetryNames.Attributes.TenantSlug].ShouldBe(TestTenants.Acme.Slug);
        server.GetTagItem(TelemetryNames.Attributes.VendorCompanyId).ShouldBe(companyId.ToString());
    }

    [Fact]
    public async Task A_platform_host_request_carries_no_tenant_id()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        using var client = ClientFor(factory, PlatformWebFactory.PlatformHost);
        var marker = Marker();

        using var response = await client.GetAsync(new Uri($"{ProbeEndpoints.PlatformLogPath}?marker={marker}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem();
        var record = ProbeRecord(telemetry, marker);
        record.TraceId.ShouldBe(server.TraceId);
        record.Properties.ShouldNotContainKey(TelemetryNames.Attributes.TenantId);
        record.Properties.ShouldNotContainKey(TelemetryNames.Attributes.TenantSlug);
        server.GetTagItem(TelemetryNames.Attributes.TenantId).ShouldBeNull();
        server.GetTagItem(TelemetryNames.Attributes.TenantSlug).ShouldBeNull();
    }

    [Fact]
    public async Task Every_response_carries_its_trace_id_as_the_correlation_id()
    {
        var admin = await AcmeAdminAsync();
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        using var acme = ClientFor(factory, "acme.localhost");
        using var platform = ClientFor(factory, PlatformWebFactory.PlatformHost);
        using var unknown = ClientFor(factory, "unknown.localhost");

        using var page = await acme.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/staff").As(admin), Ct);
        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem();
        using var console = await platform.GetAsync(new Uri($"{ProbeEndpoints.PlatformLogPath}?marker={Marker()}", UriKind.Relative), Ct);
        using var health = await acme.GetAsync(new Uri("/health", UriKind.Relative), Ct);
        using var platformHealth = await platform.GetAsync(new Uri("/health", UriKind.Relative), Ct);
        using var notFound = await unknown.GetAsync(new Uri("/admin/staff", UriKind.Relative), Ct);
        // Refused by the platform host's own rule, before tenant resolution.
        using var platformNotFound = await platform.GetAsync(new Uri("/admin/staff", UriKind.Relative), Ct);

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        CorrelationId(page).ShouldBe(server.TraceId.ToHexString());
        console.StatusCode.ShouldBe(HttpStatusCode.OK);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        platformHealth.StatusCode.ShouldBe(HttpStatusCode.OK);
        notFound.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        platformNotFound.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        foreach (var response in new[] { console, health, platformHealth, notFound, platformNotFound })
        {
            CorrelationId(response).Length.ShouldBe(32, response.RequestMessage?.RequestUri?.ToString());
            CorrelationId(response).ShouldAllBe(c => char.IsAsciiHexDigitLower(c));
            CorrelationId(response).ShouldNotBe(new string('0', 32));
        }

        new[] { page, console, health, platformHealth, notFound, platformNotFound }.Select(CorrelationId).Distinct().Count().ShouldBe(6, "one trace per request");
    }

    /// <summary>
    /// O-8: the web host starts its own trace per request. The sampled and the unsampled flag are both ignored: an unsampled
    /// <c>traceparent</c> cannot switch the request's span off (the parent-based sampler sees no remote parent).
    /// </summary>
    [Theory]
    [InlineData("01")]
    [InlineData("00")]
    public async Task A_traceparent_sent_by_a_client_does_not_become_the_request_trace_id(string flags)
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        using var client = ClientFor(factory, "acme.localhost");
        var sentTrace = ActivityTraceId.CreateRandom();
        var sentSpan = ActivitySpanId.CreateRandom();
        var marker = Marker();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ProbeEndpoints.LogPath}?marker={marker}");
        request.Headers.Add("traceparent", $"00-{sentTrace.ToHexString()}-{sentSpan.ToHexString()}-{flags}");
        request.Headers.Add("tracestate", "chosen=by-client");
        request.Headers.Add("baggage", "waslabid.tenant.id=forged");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem("the request is traced whatever flag the client sent");
        server.Recorded.ShouldBeTrue();
        server.TraceId.ShouldNotBe(sentTrace);
        server.ParentSpanId.ShouldBe(default);
        server.TraceStateString.ShouldBeNull();
        server.Baggage.ShouldBeEmpty();
        CorrelationId(response).ShouldBe(server.TraceId.ToHexString());
        ProbeRecord(telemetry, marker).Properties[TelemetryNames.Attributes.TenantId].ShouldBe(TestTenants.Acme.TenantId.ToString());
        telemetry.AllSpans.ShouldNotContain(s => s.TraceId == sentTrace);
    }

    [Fact]
    public async Task A_log_written_inside_a_circuit_event_carries_the_tenant_id()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        var companyId = Guid.NewGuid();
        var subject = $"circuit-{Marker()}";
        await using var circuit = factory.Services.CreateAsyncScope();
        circuit.ServiceProvider.GetRequiredService<TenantAccessor>().Set(TestTenants.Acme);
        circuit.ServiceProvider.GetRequiredService<ActingUserAccessor>().Set(subject);
        circuit.ServiceProvider.GetRequiredService<VendorAccessor>().Set(new VendorContext(companyId));
        var handlers = circuit.ServiceProvider.GetServices<CircuitHandler>().OrderBy(h => h.Order).ToList();
        var handler = handlers.Single(h => h.GetType().Name == "CircuitTelemetryHandler");
        handler.Order.ShouldBeGreaterThan(handlers.Single(h => h.GetType().Name == "TenantCircuitHandler").Order);
        handlers.Where(h => !ReferenceEquals(h, handler)).ShouldAllBe(h => h.Order != handler.Order, "no tie leaves the order to registration");
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Platform.Tests.CircuitProbe");
        var marker = Marker();
        var outside = Marker();

        await handler.CreateInboundActivityHandler(_ =>
        {
            Probes.Logged(logger, marker);
            return Task.CompletedTask;
        })(null!);
        Probes.Logged(logger, outside);

        var record = ProbeRecord(telemetry, marker);
        record.Properties[TelemetryNames.Attributes.TenantId].ShouldBe(TestTenants.Acme.TenantId.ToString());
        record.Properties[TelemetryNames.Attributes.TenantSlug].ShouldBe(TestTenants.Acme.Slug);
        record.Properties[TelemetryNames.Attributes.UserId].ShouldBe(subject);
        record.Properties[TelemetryNames.Attributes.VendorCompanyId].ShouldBe(companyId.ToString());
        ProbeRecord(telemetry, outside).Properties.ShouldNotContainKey(TelemetryNames.Attributes.TenantId, "the scope closes with the event");
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task The_deliberate_failure_endpoint_is_not_served_outside_development(string environment)
    {
        await using var factory = environment == "Production" ? ProductionFactory() : new PlatformWebFactory(db.AppConnectionString, environment: environment);
        using var client = ClientFor(factory, "acme.localhost");

        using var anonymous = await client.GetAsync(new Uri("/dev/throw", UriKind.Relative), Ct);
        using var signedIn = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/dev/throw").As(TestUser.AcmeAdmin), Ct);

        anonymous.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task In_development_the_deliberate_failure_endpoint_fails_with_its_fixed_message()
    {
        // The developer exception page logs the deliberate failure with its stack trace; kept out of the test output.
        await using var factory = new PlatformWebFactory(db.AppConnectionString, environment: "Development")
            .WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddFilter("Microsoft.AspNetCore.Diagnostics", LogLevel.None)));
        using var client = ClientFor(factory, "acme.localhost");

        using var response = await client.GetAsync(new Uri("/dev/throw", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Deliberate failure for the W-10 checks.");
        CorrelationId(response).Length.ShouldBe(32);
    }

    private WebApplicationFactory<Program> Factory(CapturedTelemetry telemetry) =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            telemetry.AddTo(services);
            services.AddSingleton<IStartupFilter, ProbeEndpoints>();
        }));

    /// <summary>
    /// A Production host on the test database whose key ring points at a closed local port: the ring is never read or
    /// written, so no key encrypted under the test certificate lands where the Testing hosts would fail to decrypt it.
    /// </summary>
    private WebApplicationFactory<Program> ProductionFactory()
    {
        var port = new Uri(PlatformWebFactory.UnusedOtlpEndpoint()).Port;
        var keyRing = new NpgsqlConnectionStringBuilder(TestSecrets.KeyRingConnectionString(db.AppConnectionString))
        {
            Host = "127.0.0.1",
            Port = port,
            Timeout = 1,
        };
        return new PlatformWebFactory(db.AppConnectionString, environment: "Production")
            .WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:KeyRing", keyRing.ConnectionString));
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string host) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });

    private static string CorrelationId(HttpResponseMessage response) =>
        response.Headers.TryGetValues(CorrelationHeader, out var values)
            ? values.ShouldHaveSingleItem()
            : throw new ShouldAssertException($"{response.RequestMessage?.RequestUri} answered {(int)response.StatusCode} without {CorrelationHeader}.");

    private static CapturedLog ProbeRecord(CapturedTelemetry telemetry, string marker) =>
        telemetry.Logs.Where(l => l.Template == Probes.Template && l.Properties.GetValueOrDefault("Marker") == marker).ShouldHaveSingleItem();

    private static string Marker() => Guid.NewGuid().ToString("N");

    private async Task<TestUser> AcmeAdminAsync()
    {
        var admin = new TestUser($"admin-{Guid.NewGuid():N}", ["acme"], "en");
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, admin.Subject, $"{admin.Subject}@acme.test", [TenantRoles.TenantAdmin], "active", Ct);
        return admin;
    }

    /// <summary>
    /// Test-only endpoints that write one log record with a marker: anonymous on a tenant host, under the Vendor policy, and
    /// anonymous on the platform host (under <c>/platform</c>, the only paths that host serves).
    /// </summary>
    private sealed class ProbeEndpoints : IStartupFilter
    {
        public const string LogPath = "/test/telemetry/log";
        public const string VendorLogPath = "/test/telemetry/vendor-log";
        public const string PlatformLogPath = "/platform/test/telemetry/log";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = app.Properties.TryGetValue("__EndpointRouteBuilder", out var value) && value is IEndpointRouteBuilder routeBuilder
                ? routeBuilder
                : throw new InvalidOperationException("The host did not expose its endpoint route builder.");
            endpoints.MapGet(LogPath, Log).AllowAnonymous();
            endpoints.MapGet(VendorLogPath, Log).RequireAuthorization(VendorPolicies.Vendor);
            endpoints.MapGet(PlatformLogPath, Log).AllowAnonymous();
        };

        private static IResult Log(string marker, ILogger<ProbeEndpoints> logger)
        {
            Probes.Logged(logger, marker);
            return Results.Ok();
        }
    }

    private static partial class Probes
    {
        public const string Template = "Telemetry context probe {Marker}";

        [LoggerMessage(Level = LogLevel.Information, Message = Template)]
        public static partial void Logged(ILogger logger, string marker);
    }
}
