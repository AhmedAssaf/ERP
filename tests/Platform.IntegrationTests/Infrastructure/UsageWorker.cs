using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Operations;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Operations.Usage;
using Platform.Modules.Tenancy;
using Platform.Shared;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// The worker's part of the usage metrics (spec 6.4), wired as <c>Platform.Worker</c> wires it, without Hangfire: the
/// tenancy module (slugs), the Identity module's activity counts, the Operations module with the usage job and its
/// snapshot, and a meter factory of its own. One scope stands for one job run, with neither a tenant nor a vendor context.
/// </summary>
internal sealed class UsageWorker : IAsyncDisposable
{
    private readonly ServiceProvider _root;

    public UsageWorker(string appConnectionString, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        services.AddPlatformShared();
        services.AddAuditModule(appConnectionString);
        services.AddTenancyModule(appConnectionString);
        services.AddIdentityActivityCounts(appConnectionString);
        services.AddOperationsModule(appConnectionString);
        services.AddOperationsUsageMetrics();
        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public IServiceProvider Services => _root;

    public IMeterFactory Meters => _root.GetRequiredService<IMeterFactory>();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var scope = _root.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<UsageMetricsJob>().RunAsync(cancellationToken);
    }

    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        await using var scope = _root.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<UsageMetricsJob>().PruneAsync(cancellationToken);
    }

    public async Task<UsageCounts?> LatestAsync(CancellationToken cancellationToken)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUsageLog>().LatestAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _root.DisposeAsync();
}
