using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Modules.Audit;
using Platform.Shared;
using Platform.Shared.Jobs;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>An in-process worker: the modules, a Hangfire server on the test database, and a client to enqueue with.</summary>
internal sealed class JobServerHost : IAsyncDisposable
{
    private readonly IHost _host;

    private JobServerHost(IHost host) => _host = host;

    public string ServerName { get; private init; } = string.Empty;

    public IServiceProvider Services => _host.Services;

    public JobStorage Storage => _host.Services.GetRequiredService<JobStorage>();

    public static async Task<JobServerHost> StartAsync(
        string appConnectionString, Action<IServiceCollection>? configure = null, CancellationToken cancellationToken = default)
    {
        var serverName = $"test-{Guid.NewGuid():N}";
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();
        builder.Services.AddPlatformShared();
        builder.Services.AddAuditModule(appConnectionString);
        builder.Services.AddJobServer(appConnectionString, options =>
        {
            options.ServerName = serverName;
            options.WorkerCount = 4;
        });
        configure?.Invoke(builder.Services);

        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        return new JobServerHost(host) { ServerName = serverName };
    }

    public AsyncServiceScope ScopeFor(TenantContext? tenant)
    {
        var scope = _host.Services.CreateAsyncScope();
        if (tenant is not null)
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(tenant);
        }

        return scope;
    }

    /// <summary>Waits until the job succeeded; a failure fails the test with the job's exception message.</summary>
    public async Task WaitForSuccessAsync(string jobId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            string? state;
            using (var connection = Storage.GetConnection())
            {
                state = connection.GetStateData(jobId)?.Name;
            }

            if (state == "Succeeded")
            {
                return;
            }

            if (state is "Failed" or "Scheduled")
            {
                var failure = Storage.GetMonitoringApi().JobDetails(jobId).History
                    .FirstOrDefault(h => h.StateName == "Failed");
                if (failure is not null)
                {
                    throw new InvalidOperationException(
                        $"Job {jobId} failed: {string.Join("; ", failure.Data.Select(d => $"{d.Key}={d.Value}"))}");
                }
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"Job {jobId} did not succeed within 60 seconds.");
    }

    /// <summary>True once this host's Hangfire server has announced itself in storage.</summary>
    public bool ServerIsRegistered() =>
        Storage.GetMonitoringApi().Servers().Any(s => s.Name.StartsWith(ServerName, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }
}

internal static class JobStorageConnectionExtensions
{
    public static string? GetParameter(this JobStorage storage, string jobId, string name)
    {
        using var connection = storage.GetConnection();
        return connection.GetJobParameter(jobId, name);
    }
}
