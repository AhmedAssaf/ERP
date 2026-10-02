using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Platform.Shared.Telemetry;

namespace Platform.UnitTests.Shared;

/// <summary>W-10 (plan task 1): where the OTLP endpoint and the resource's environment come from.</summary>
public class TelemetryModuleTests
{
    [Fact]
    public void Without_either_setting_there_is_no_endpoint()
    {
        TelemetryModule.OtlpEndpoint(Configuration()).ShouldBeNull();
    }

    [Fact]
    public void The_standard_variable_is_used_when_the_own_setting_is_absent()
    {
        var configuration = Configuration((TelemetryModule.StandardOtlpEndpointVariable, "http://collector:4317"));

        TelemetryModule.OtlpEndpoint(configuration).ShouldBe(new Uri("http://collector:4317"));
    }

    [Fact]
    public void The_own_setting_wins_over_the_standard_variable()
    {
        var configuration = Configuration(
            (TelemetryModule.OtlpEndpointSetting, "http://localhost:4317"),
            (TelemetryModule.StandardOtlpEndpointVariable, "http://collector:4317"));

        TelemetryModule.OtlpEndpoint(configuration).ShouldBe(new Uri("http://localhost:4317"));
    }

    [Fact]
    public void An_explicitly_empty_own_setting_means_off_without_falling_back()
    {
        var configuration = Configuration(
            (TelemetryModule.OtlpEndpointSetting, string.Empty),
            (TelemetryModule.StandardOtlpEndpointVariable, "http://collector:4317"));

        TelemetryModule.OtlpEndpoint(configuration).ShouldBeNull();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_development_and_testing_a_host_without_an_endpoint_does_not_start(string environment)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = environment });

        var refused = Should.Throw<InvalidOperationException>(() => builder.AddPlatformTelemetry(TelemetryNames.Services.Worker));

        refused.Message.ShouldContain(TelemetryModule.OtlpEndpointSetting);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void In_development_and_testing_a_host_without_an_endpoint_registers_telemetry(string environment)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = environment });

        Should.NotThrow(() => builder.AddPlatformTelemetry(TelemetryNames.Services.Worker));
    }

    /// <summary>
    /// W-10 final fix wave (E): the default host turns on Microsoft.Extensions.Logging activity tracking, which adds TraceId,
    /// SpanId and ParentId as a scope to every record; the OTLP record carries trace_id and span_id itself, so tracking is off.
    /// </summary>
    [Fact]
    public void Logging_adds_no_activity_ids_of_its_own()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Services.BuildServiceProvider().GetRequiredService<IOptions<LoggerFactoryOptions>>().Value.ActivityTrackingOptions
            .ShouldNotBe(ActivityTrackingOptions.None, "the default host tracks activities, so this test can fail");

        builder.AddPlatformTelemetry(TelemetryNames.Services.Worker);

        using var services = builder.Services.BuildServiceProvider();
        services.GetRequiredService<IOptions<LoggerFactoryOptions>>().Value.ActivityTrackingOptions.ShouldBe(ActivityTrackingOptions.None);
    }

    /// <summary>
    /// W-10 follow-up (2026-10-02; spec section 5.5): the collector's Elasticsearch exporter drops cumulative histograms, so
    /// with the SDK's default (cumulative) no request, database or job duration ever reached <c>metrics-*</c>. The OTLP metric
    /// reader of both hosts (the same registration) prefers delta. The reader is read from the built provider, since the
    /// preference the OTLP exporter hands its reader is what leaves, not what an options object says.
    /// </summary>
    [Fact]
    public void The_otlp_metric_reader_exports_delta_temporality()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Testing" });
        // A closed loopback port: nothing is listening, the reader is only built and shut down.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [TelemetryModule.OtlpEndpointSetting] = "http://127.0.0.1:9" });
        builder.AddPlatformTelemetry(TelemetryNames.Services.Worker);

        using var services = builder.Services.BuildServiceProvider();
        var provider = services.GetRequiredService<MeterProvider>();
        var reader = provider.GetType().GetProperty("Reader", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider) as MetricReader;

        reader.ShouldNotBeNull("the OTLP exporter registers one metric reader; OpenTelemetry's internal Reader property may have moved");
        reader.TemporalityPreference.ShouldBe(MetricReaderTemporalityPreference.Delta);
    }

    /// <summary>
    /// W-10 follow-up, fix round 1: the OTLP exporter's span event limit is 0 in both hosts' configuration, whatever an
    /// environment variable or an earlier configuration source says, so no span event leaves (spec O-10, 5.2).
    /// </summary>
    [Fact]
    public void The_span_event_limit_is_zero_even_when_configured_higher()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [TelemetryModule.SpanEventCountLimit] = "128" });

        builder.AddPlatformTelemetry(TelemetryNames.Services.Web);

        builder.Configuration[TelemetryModule.SpanEventCountLimit].ShouldBe("0");
    }

    [Theory]
    [InlineData("localhost:4317")]
    [InlineData("not a url")]
    [InlineData("ftp://collector:4317")]
    public void An_endpoint_that_is_not_an_http_url_stops_the_host_and_names_the_setting(string value)
    {
        var refused = Should.Throw<InvalidOperationException>(() => TelemetryModule.OtlpEndpoint(Configuration((TelemetryModule.OtlpEndpointSetting, value))));

        refused.Message.ShouldContain(TelemetryModule.OtlpEndpointSetting);
        refused.Message.ShouldNotContain(value);
    }

    [Fact]
    public void The_environment_is_the_host_environment_in_lower_case_unless_set()
    {
        var resource = TelemetryResource.For("waslabid-web", new Environment("Production"), Configuration());

        resource.DeploymentEnvironment.ShouldBe("production");
        resource.Attributes[TelemetryNames.Resource.DeploymentEnvironment].ShouldBe("production");
    }

    [Fact]
    public void Telemetry_environment_names_the_deployment()
    {
        var resource = TelemetryResource.For("waslabid-worker", new Environment("Production"), Configuration((TelemetryModule.EnvironmentSetting, "pilot")));

        resource.DeploymentEnvironment.ShouldBe("pilot");
        resource.ServiceName.ShouldBe("waslabid-worker");
        resource.ServiceVersion.ShouldNotBeNullOrWhiteSpace();
        Guid.TryParse(resource.ServiceInstanceId, out _).ShouldBeTrue();
    }

    [Fact]
    public void Each_host_gets_its_own_instance_id()
    {
        var first = TelemetryResource.For("waslabid-web", new Environment("Testing"), Configuration());
        var second = TelemetryResource.For("waslabid-web", new Environment("Testing"), Configuration());

        first.ServiceInstanceId.ShouldNotBe(second.ServiceInstanceId);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Platform.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
