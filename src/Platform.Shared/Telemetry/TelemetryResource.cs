using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Platform.Shared.Telemetry;

/// <summary>
/// The resource every span, metric and log record of one host carries (W-10, spec 5.1): <c>service.name</c>,
/// <c>service.version</c>, <c>service.instance.id</c> and <c>deployment.environment.name</c>. One instance per host, shared by
/// the OpenTelemetry SDK (traces, metrics) and Serilog's OTLP sink (logs), so the three signals of a process match.
/// </summary>
public sealed class TelemetryResource
{
    private TelemetryResource(string serviceName, string serviceVersion, string serviceInstanceId, string deploymentEnvironment)
    {
        ServiceName = serviceName;
        ServiceVersion = serviceVersion;
        ServiceInstanceId = serviceInstanceId;
        DeploymentEnvironment = deploymentEnvironment;
        Attributes = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [TelemetryNames.Resource.ServiceName] = serviceName,
            [TelemetryNames.Resource.ServiceVersion] = serviceVersion,
            [TelemetryNames.Resource.ServiceInstanceId] = serviceInstanceId,
            [TelemetryNames.Resource.DeploymentEnvironment] = deploymentEnvironment,
        };
    }

    public string ServiceName { get; }

    public string ServiceVersion { get; }

    public string ServiceInstanceId { get; }

    public string DeploymentEnvironment { get; }

    /// <summary>The four attributes by their semantic-convention names.</summary>
    public IReadOnlyDictionary<string, object> Attributes { get; }

    /// <summary>
    /// The version is the entry assembly's informational version (the host's: <c>Platform.Web</c> or <c>Platform.Worker</c>;
    /// the SDK appends the git commit), falling back to this assembly's when there is no entry assembly. The environment is
    /// <c>Telemetry:Environment</c>, by default the host environment name in lower case. The instance id is new per process.
    /// </summary>
    internal static TelemetryResource For(string serviceName, IHostEnvironment environment, IConfiguration configuration)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(TelemetryResource).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var deployment = configuration[TelemetryModule.EnvironmentSetting];
        if (string.IsNullOrWhiteSpace(deployment))
        {
            deployment = environment.EnvironmentName.ToLower(CultureInfo.InvariantCulture);
        }

        return new TelemetryResource(serviceName, version, Guid.NewGuid().ToString(), deployment);
    }
}
