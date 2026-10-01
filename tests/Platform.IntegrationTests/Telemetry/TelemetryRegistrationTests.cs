using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Telemetry;
using Platform.Web.Usage;
using Serilog.Extensions.Logging;
using Serilog.Sinks.OpenTelemetry;

namespace Platform.IntegrationTests.Telemetry;

/// <summary>
/// W-10, plan task 1 (spec O-3, O-5, O-6, O-15, O-16; Q2 decided 2026-10-01: Serilog kept for logs): both hosts register
/// the OpenTelemetry SDK for traces and metrics and Serilog, as a logging provider only, for logs; the OTLP exporters exist
/// only when an endpoint is configured; health, liveness and framework requests are not traced.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class TelemetryRegistrationTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_tenant_request_produces_one_server_span_with_its_route_and_status()
    {
        var admin = await AcmeAdminAsync();
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        using var client = ClientFor(factory, "acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/staff").As(admin), Ct);
        // The sentinel: a second, unauthenticated request (401). Once its span is in, the first request's spans are all in.
        using var sentinel = await client.GetAsync(new Uri("/admin/staff", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        sentinel.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var spans = await telemetry.WaitForServerSpansAsync(factory.Services, 2, Ct);
        spans.Count.ShouldBe(2, "one server span per request");
        var server = spans.Where(s => Equals(s.GetTagItem("http.response.status_code"), 200)).ShouldHaveSingleItem();
        server.GetTagItem("http.route").ShouldBe("/admin/staff");
    }

    /// <remarks>
    /// <c>/alive</c> is mapped by plan task 4; until then it answers whatever the pipeline answers, and is still not traced.
    /// A traced request sent last marks the moment every earlier request has ended: after its span, nothing else is left.
    /// </remarks>
    [Fact]
    public async Task Health_alive_and_framework_requests_produce_no_span()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        using var client = ClientFor(factory, "acme.localhost");
        var assets = StaticAssetRoutes(factory.Services);
        var content = assets.First(r => r.StartsWith("/_content/", StringComparison.Ordinal));
        // A static asset outside /_framework and /_content, such as the scoped CSS bundle /Platform.Web.styles.css.
        var elsewhere = assets.First(r => !r.StartsWith("/_content/", StringComparison.Ordinal) && !r.StartsWith("/_framework/", StringComparison.Ordinal));
        telemetry.Clear();

        (await client.GetAsync(new Uri("/health", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.GetAsync(new Uri("/alive", UriKind.Relative), Ct);
        (await client.GetAsync(new Uri("/_framework/blazor.web.js", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(new Uri(content, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(new Uri(elsewhere, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PostAsync(new Uri("/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), null, Ct);
        // The circuit's own request: on a browser it is the WebSocket that lives as long as the circuit.
        await client.GetAsync(new Uri("/_blazor?id=unknown-connection", UriKind.Relative), Ct);
        await client.GetAsync(new Uri("/admin/staff", UriKind.Relative), Ct);

        var sentinel = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem("only the last request is traced");
        sentinel.GetTagItem("http.route").ShouldBe("/admin/staff");
        telemetry.SpansOf(factory.Services).ShouldAllBe(span => span.TraceId == sentinel.TraceId);
    }

    [Fact]
    public async Task A_database_call_during_a_request_is_a_child_span_without_parameter_values()
    {
        var admin = await AcmeAdminAsync();
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        using var client = ClientFor(factory, "acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/staff").As(admin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem();
        var commands = telemetry.SpansOf(factory.Services).Where(s => s.Source.Name == "Npgsql" && s.TraceId == server.TraceId).ToList();
        commands.ShouldNotBeEmpty();
        commands.ShouldAllBe(s => s.ParentSpanId != default);
        commands.ShouldContain(s => Statement(s).Contains("set_config('app.tenant_id'", StringComparison.Ordinal), "the tenant context is set with placeholders");
        var tenantId = TestTenants.Acme.TenantId;
        var leaked = commands
            .SelectMany(s => s.TagObjects)
            .Where(t => t.Value?.ToString() is { } value
                && (value.Contains(tenantId.ToString("D"), StringComparison.OrdinalIgnoreCase) || value.Contains(tenantId.ToString("N"), StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Key)
            .ToList();
        leaked.ShouldBeEmpty();
    }

    /// <summary>
    /// Database work outside any request or job (Hangfire polling, heartbeats, locks, the key-ring refresh) starts no trace
    /// of its own; a command inside a request still gets its child span.
    /// </summary>
    [Fact]
    public async Task A_database_call_outside_any_activity_produces_no_span()
    {
        var admin = await AcmeAdminAsync();
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        using var client = ClientFor(factory, "acme.localhost");
        var marker = $"root_probe_{Guid.NewGuid():N}";

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            await using var connection = new NpgsqlConnection(db.AppConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand($"select 1 as {marker}", connection);
            await command.ExecuteScalarAsync(Ct);
        }
        finally
        {
            Activity.Current = previous;
        }

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/staff").As(admin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem();
        telemetry.SpansOf(factory.Services).ShouldContain(s => s.Source.Name == "Npgsql" && s.TraceId == server.TraceId);
        telemetry.AllSpans.ShouldNotContain(s => Statement(s).Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Both_hosts_name_their_service_version_and_environment_in_the_resource()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        await using var worker = await JobServerHost.StartAsync(
            db.AppConnectionString, configureHost: builder => builder.AddPlatformTelemetry(TelemetryNames.Services.Worker), cancellationToken: Ct);

        foreach (var (host, serviceName) in new[] { (factory.Services, "waslabid-web"), (worker.Services, "waslabid-worker") })
        {
            var resource = host.GetRequiredService<TracerProvider>().GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value?.ToString());
            resource[TelemetryNames.Resource.ServiceName].ShouldBe(serviceName);
            resource[TelemetryNames.Resource.ServiceVersion].ShouldNotBeNullOrWhiteSpace();
            resource[TelemetryNames.Resource.ServiceInstanceId].ShouldNotBeNullOrWhiteSpace();
            resource[TelemetryNames.Resource.DeploymentEnvironment].ShouldNotBeNullOrWhiteSpace();
            // The log records leave through Serilog's OTLP sink with the same resource as the spans and metrics.
            var logs = host.GetRequiredService<TelemetryResource>().Attributes;
            foreach (var key in logs.Keys)
            {
                logs[key].ToString().ShouldBe(resource[key], key);
            }
        }

        factory.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
            .Single(a => a.Key == TelemetryNames.Resource.DeploymentEnvironment).Value.ShouldBe("testing", "the host environment in lower case by default");
    }

    /// <summary>
    /// The <c>WaslaBid.*</c> wildcard picks up meters created elsewhere, such as the usage meter of PR #6 (spec 6.3), and the
    /// built-in runtime meter is registered by name.
    /// </summary>
    [Fact]
    public async Task The_usage_and_runtime_meters_are_collected()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        factory.Services.GetRequiredService<ConnectedCircuits>().Add(new object(), "acme", UsageKind.Staff, $"user-{Guid.NewGuid():N}", () => false);

        var metrics = telemetry.CollectMetrics(factory.Services);

        metrics.ShouldContain(m => m.MeterName == TelemetryNames.UsageMeter && m.Name == TelemetryNames.CircuitsConnected);
        metrics.ShouldContain(m => m.MeterName == "System.Runtime");
    }

    [Fact]
    public async Task Without_an_otlp_endpoint_the_host_starts_and_registers_no_otlp_exporter()
    {
        List<ServiceDescriptor> without = [];
        List<ServiceDescriptor> with = [];
        // The factory sets Telemetry:OtlpEndpoint to empty, which means off: the standard variable (read through configuration,
        // as the hosts read environment variables) is then ignored, so a developer's OTEL_EXPORTER_OTLP_ENDPOINT changes nothing.
        await using (var plain = new PlatformWebFactory(db.AppConnectionString)
            .WithWebHostBuilder(builder => builder
                .UseSetting(TelemetryModule.StandardOtlpEndpointVariable, PlatformWebFactory.UnusedOtlpEndpoint())
                .ConfigureTestServices(services => without = [.. services])))
        {
            using var client = plain.CreateClient();
            (await client.GetAsync(new Uri("/health", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await using (var exporting = new PlatformWebFactory(db.AppConnectionString)
            .WithWebHostBuilder(builder => builder
                .UseSetting(TelemetryModule.OtlpEndpointSetting, PlatformWebFactory.UnusedOtlpEndpoint())
                .ConfigureTestServices(services => with = [.. services])))
        {
            _ = exporting.Server;
        }

        without.ShouldNotBeEmpty();
        without.Where(ReferencesOtlp).ShouldBeEmpty();
        // The same check finds the exporters once an endpoint is set: span and metric exporters, and the log sink.
        with.ShouldContain(d => ReferencesOtlp(d) && Types(d).Any(t => t.Assembly == typeof(OtlpExporterOptions).Assembly));
        with.ShouldContain(d => Types(d).Contains(typeof(OtlpLogSink)));
    }

    [Fact]
    public async Task With_the_collector_unreachable_requests_still_succeed()
    {
        var admin = await AcmeAdminAsync();
        var factory = new PlatformWebFactory(db.AppConnectionString)
            .WithWebHostBuilder(builder => builder.UseSetting(TelemetryModule.OtlpEndpointSetting, PlatformWebFactory.UnusedOtlpEndpoint()));
        using (var client = ClientFor(factory, "acme.localhost"))
        {
            for (var i = 0; i < 20; i++)
            {
                using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/staff").As(admin), Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, $"request {i + 1}");
            }
        }

        await Should.NotThrowAsync(async () => await factory.DisposeAsync());
    }

    /// <summary>
    /// Outside Development and Testing the console is cleared (O-16), so without a collector every log line would be lost:
    /// the host does not start, and says which setting is missing. An explicitly empty setting is no endpoint.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Outside_development_a_host_without_an_otlp_endpoint_does_not_start(string? endpoint)
    {
        using var factory = new PlatformWebFactory("Host=unused;Database=unused", environment: "Production")
            .WithWebHostBuilder(builder => builder.UseSetting(TelemetryModule.OtlpEndpointSetting, endpoint));

        var refused = Should.Throw<InvalidOperationException>(() => factory.Server);

        refused.Message.ShouldContain(TelemetryModule.OtlpEndpointSetting);
        refused.Message.ShouldNotContain("http", Case.Insensitive);
    }

    /// <summary>
    /// Read from the registrations of a host that is built but stopped before its first hosted service, on a database that
    /// does not exist: a Production host that started on the shared test database would store a key ring key under the test
    /// certificate, which every later Testing host then fails to decrypt (logged as errors in their output).
    /// </summary>
    [Fact]
    public void Outside_development_no_console_log_provider_is_registered()
    {
        List<ServiceDescriptor> services = [];
        using var factory = new PlatformWebFactory("Host=unused;Database=unused", environment: "Production")
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(s =>
            {
                services = [.. s];
                s.Insert(0, ServiceDescriptor.Singleton<IHostedService, StopBeforeStart>());
            }));

        Should.Throw<InvalidOperationException>(() => factory.Server).Message.ShouldBe(StopBeforeStart.Message);

        var providers = services.Where(d => d.ServiceType == typeof(ILoggerProvider)).ToList();
        providers.ShouldNotContain(d => d.ImplementationType == typeof(ConsoleLoggerProvider));
        // Only the Serilog provider, registered by a factory (TelemetryModule): every other provider was cleared.
        providers.ShouldHaveSingleItem().ImplementationFactory.ShouldNotBeNull();
    }

    [Fact]
    public void In_development_the_console_log_provider_stays()
    {
        List<ServiceDescriptor> services = [];
        using var factory = new PlatformWebFactory(db.AppConnectionString, environment: "Development")
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(s => services = [.. s]));

        var providers = factory.Services.GetServices<ILoggerProvider>().ToList();

        providers.ShouldContain(p => p is ConsoleLoggerProvider);
        providers.ShouldContain(p => p is SerilogLoggerProvider);
        // The registration check of the Production test finds the console here.
        services.ShouldContain(d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationType == typeof(ConsoleLoggerProvider));
    }

    /// <summary>
    /// Q2 (Serilog kept): <c>UseSerilog()</c> or <c>services.AddSerilog()</c> would replace the logger factory and silently
    /// bypass the <c>LoggerFilterOptions</c> rules W-24's N-10 guard relies on; Serilog is one provider among others.
    /// </summary>
    [Fact]
    public async Task Serilog_is_a_logging_provider_and_never_replaces_the_logger_factory()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        await using var worker = await JobServerHost.StartAsync(
            db.AppConnectionString, configureHost: builder => builder.AddPlatformTelemetry(TelemetryNames.Services.Worker), cancellationToken: Ct);

        foreach (var host in new[] { factory.Services, worker.Services })
        {
            host.GetRequiredService<ILoggerFactory>().ShouldBeOfType<LoggerFactory>();
            host.GetServices<ILoggerProvider>().ShouldContain(p => p is SerilogLoggerProvider);
        }
    }

    /// <summary>
    /// Serilog's own minimum level is Verbose, so the Microsoft.Extensions.Logging rules (appsettings <c>Logging:LogLevel</c>,
    /// W-24's cap) are the only gate: a Debug record and a framework Information record (Microsoft.AspNetCore at Warning)
    /// never reach the sinks.
    /// </summary>
    [Fact]
    public async Task Logging_levels_from_configuration_gate_the_serilog_provider()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = Factory(telemetry);
        var loggers = factory.Services.GetRequiredService<ILoggerFactory>();
        var marker = Guid.NewGuid().ToString("N");

        var probe = loggers.CreateLogger("Platform.Tests.Probe");
        Probes.Debug(probe, marker);
        Probes.Information(probe, marker);
        var framework = loggers.CreateLogger("Microsoft.AspNetCore.Probe");
        Probes.Framework(framework, marker);

        var captured = telemetry.Logs.Where(l => l.Properties.GetValueOrDefault("Marker") == marker).ShouldHaveSingleItem();
        captured.Template.ShouldBe("information probe {Marker}");
        captured.Category.ShouldBe("Platform.Tests.Probe");
    }

    /// <summary>
    /// Serilog takes the trace and span ids from <see cref="Activity.Current"/> when the record is written; the OTLP sink
    /// sends them as the log record's trace and span ids (IncludedData TraceIdField and SpanIdField).
    /// </summary>
    [Fact]
    public async Task A_log_record_written_during_a_request_carries_its_trace_and_span_ids()
    {
        var telemetry = new CapturedTelemetry();
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            telemetry.AddTo(services);
            services.AddSingleton<IStartupFilter, RequestLogStartupFilter>();
        }));
        using var client = ClientFor(factory, "acme.localhost");

        await client.GetAsync(new Uri("/admin/staff", UriKind.Relative), Ct);

        var server = (await telemetry.WaitForServerSpansAsync(factory.Services, 1, Ct)).ShouldHaveSingleItem();
        var record = telemetry.Logs.Single(l => l.Template == RequestLogStartupFilter.Template);
        record.TraceId.ShouldBe(server.TraceId);
        record.SpanId.ShouldBe(server.SpanId);
    }

    private WebApplicationFactory<Program> Factory(CapturedTelemetry telemetry) =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(telemetry.AddTo));

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string host) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });

    private async Task<TestUser> AcmeAdminAsync()
    {
        var admin = new TestUser($"admin-{Guid.NewGuid():N}", ["acme"], "en");
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, admin.Subject, $"{admin.Subject}@acme.test", [TenantRoles.TenantAdmin], "active", Ct);
        return admin;
    }

    private static List<string> StaticAssetRoutes(IServiceProvider host) =>
        [.. host.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => e.Metadata.GetMetadata<StaticAssetDescriptor>())
            .OfType<StaticAssetDescriptor>()
            .Select(d => "/" + d.Route)
            .Distinct()];

    private static string Statement(Activity span) => span.GetTagItem("db.query.text") as string ?? string.Empty;

    private static bool ReferencesOtlp(ServiceDescriptor descriptor) =>
        Types(descriptor).Any(t =>
            t.Assembly == typeof(OtlpExporterOptions).Assembly
            || t.Assembly == typeof(OtlpProtocol).Assembly
            || t == typeof(OtlpLogSink));

    /// <summary>The service type, implementation type or instance type of a descriptor, and their generic arguments.</summary>
    private static List<Type> Types(ServiceDescriptor descriptor)
    {
        var types = new List<Type?>
        {
            descriptor.ServiceType,
            descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType,
            (descriptor.IsKeyedService ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance)?.GetType(),
        };
        return [.. types.OfType<Type>().SelectMany(t => t.GetGenericArguments().Prepend(t))];
    }

    /// <summary>Stops a host after it is built and before any other hosted service starts.</summary>
    private sealed class StopBeforeStart : IHostedService
    {
        public const string Message = "Stopped by the test once the host was built.";

        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException(Message);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Writes one Information record at the very start of every request, inside the request's activity.</summary>
    private sealed class RequestLogStartupFilter(ILogger<RequestLogStartupFilter> logger) : IStartupFilter
    {
        public const string Template = Probes.RequestTemplate;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                Probes.Request(logger, context.Request.Path.Value);
                await nextMiddleware(context);
            });
            next(app);
        };
    }

    private static partial class Probes
    {
        public const string RequestTemplate = "Telemetry probe for {RequestPath}";

        [LoggerMessage(Level = LogLevel.Debug, Message = "debug probe {Marker}")]
        public static partial void Debug(ILogger logger, string marker);

        [LoggerMessage(Level = LogLevel.Information, Message = "information probe {Marker}")]
        public static partial void Information(ILogger logger, string marker);

        [LoggerMessage(Level = LogLevel.Information, Message = "framework probe {Marker}")]
        public static partial void Framework(ILogger logger, string marker);

        [LoggerMessage(Level = LogLevel.Information, Message = RequestTemplate)]
        public static partial void Request(ILogger logger, string? requestPath);
    }
}
