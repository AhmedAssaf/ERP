using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.Server;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Platform.Shared.Data;
using Platform.Shared.Tenancy;

namespace Platform.Shared.Jobs;

/// <summary>Settings of the Hangfire server in the worker host.</summary>
public sealed class JobServerSettings
{
    /// <summary>Server name shown on the dashboard; Hangfire appends a unique suffix per process.</summary>
    public string? ServerName { get; set; }

    /// <summary>Concurrent jobs per server; Hangfire's default when null.</summary>
    public int? WorkerCount { get; set; }

    /// <summary>
    /// How often the server checks for due scheduled/delayed jobs (Hangfire's default, 15 seconds, when null).
    /// Tests that need a retry to run again quickly (for example plan task 4's job-failure alert, with
    /// <c>AutomaticRetryAttribute.DelaysInSeconds</c> set to zero) shorten this instead of waiting out the default.
    /// </summary>
    public TimeSpan? SchedulePollingInterval { get; set; }
}

/// <summary>
/// Hangfire on PostgreSQL (schema <c>hangfire</c>, spec D-6). Web hosts only enqueue (<see cref="AddJobClient"/>);
/// the worker host also runs the server (<see cref="AddJobServer"/>). Registration avoids Hangfire's static
/// GlobalConfiguration so several hosts can share one process (tests), each with its own storage and activator.
/// </summary>
public static class JobsModule
{
    public const string SchemaName = "hangfire";

    /// <summary>The key of Hangfire's own named data source (<see cref="DataSourceNames.Jobs"/>).</summary>
    private const string DataSourceKey = "Platform.Shared.Jobs";

    /// <summary>
    /// Installs or upgrades Hangfire's tables in schema <c>hangfire</c> as the migration owner (W-36): the migrator runs this
    /// after the platform migrations, whose 0008 grants the application role what enqueueing and the worker need (and sets
    /// the default privileges for tables a later Hangfire version adds). The hosts never prepare the schema, so no runtime
    /// role owns or creates Hangfire's tables. Use a connection of its own: Hangfire's scripts set the search path.
    /// </summary>
    public static void InstallSchema(NpgsqlConnection ownerConnection)
    {
        ArgumentNullException.ThrowIfNull(ownerConnection);
        PostgreSqlObjectsInstaller.Install(ownerConnection, SchemaName);
    }

    /// <summary>Registers the storage and a scoped <see cref="IBackgroundJobClient"/> that stamps the current tenant.</summary>
    public static IServiceCollection AddJobClient(this IServiceCollection services, string connectionString) =>
        AddJobClient(services, connectionString, failFast: false);

    /// <summary>
    /// Registers the client plus a Hangfire server whose jobs run in their own DI scope as the enqueuing tenant, each inside
    /// its span and log scope (<see cref="JobTelemetryFilter"/>, W-10).
    /// </summary>
    public static IServiceCollection AddJobServer(
        this IServiceCollection services, string connectionString, Action<JobServerSettings>? configure = null)
    {
        AddJobClient(services, connectionString, failFast: true);
        var settings = new JobServerSettings();
        configure?.Invoke(settings);
        services.AddSingleton<IServerFilter, JobTelemetryFilter>();

        services.AddSingleton<IHostedService>(sp =>
        {
            // This host's own DI-registered state and server filters (e.g. Operations' job-failure alert, the job telemetry),
            // on top of Hangfire's process-wide defaults. Never Hangfire's static GlobalJobFilters.Filters: several Hangfire
            // servers can share one process (tests build one per test host), and a filter registered for one must not run
            // on another's jobs. An instance registered under two of these interfaces is still one filter.
            var hostFilters = sp.GetServices<IElectStateFilter>().Cast<object>()
                .Concat(sp.GetServices<IApplyStateFilter>())
                .Concat(sp.GetServices<IServerFilter>())
                .Distinct(ReferenceEqualityComparer.Instance)
                .ToArray();

            var options = new BackgroundJobServerOptions
            {
                Activator = new TenantJobActivator(sp.GetRequiredService<IServiceScopeFactory>()),
                // Never empty: the job telemetry filter is always among them.
                FilterProvider = new HostScopedFilterProvider(JobFilterProviders.Providers, hostFilters),
            };
            if (settings.ServerName is not null)
            {
                options.ServerName = settings.ServerName;
            }

            if (settings.WorkerCount is { } workers)
            {
                options.WorkerCount = workers;
            }

            if (settings.SchedulePollingInterval is { } pollingInterval)
            {
                options.SchedulePollingInterval = pollingInterval;
            }

            return new BackgroundJobServerHostedService(
                sp.GetRequiredService<JobStorage>(), options, [], sp.GetService<IHostApplicationLifetime>());
        });
        return services;
    }

    private static IServiceCollection AddJobClient(IServiceCollection services, string connectionString, bool failFast)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // A web host may start before the database is reachable and uses the storage once it is; the worker must not run
        // without storage, so it fails at start instead.
        // Named (W-10): an unnamed data source is named after its connection string in metrics and spans. The container owns
        // and disposes it.
        services.TryAddKeyedSingleton(DataSourceKey, (_, _) => new NpgsqlDataSourceBuilder(connectionString) { Name = DataSourceNames.Jobs }.Build());
        services.TryAddSingleton<JobStorage>(sp =>
        {
            var options = new PostgreSqlStorageOptions
            {
                SchemaName = SchemaName,
                // W-36: the migrator installs the tables as the owner (InstallSchema); neither host may create them.
                PrepareSchemaIfNecessary = false,
                EnableLongPolling = true,
                AllowDegradedModeWithoutStorage = !failFast,
            };
            return new PostgreSqlStorage(new DataSourceConnectionFactory(sp.GetRequiredKeyedService<NpgsqlDataSource>(DataSourceKey)), options);
        });
        services.TryAddScoped<IBackgroundJobClient>(sp => new BackgroundJobClient(
            sp.GetRequiredService<JobStorage>(),
            new TenantStampingFilterProvider(JobFilterProviders.Providers, new TenantJobFilter(sp.GetRequiredService<ITenantAccessor>()))));
        return services;
    }

    /// <summary>Hangfire's connections from the named data source, never from a bare connection string.</summary>
    private sealed class DataSourceConnectionFactory(NpgsqlDataSource dataSource) : IConnectionFactory
    {
        public NpgsqlConnection GetOrCreateConnection() => dataSource.CreateConnection();
    }

    /// <summary>The global filters (retries, culture, continuations) plus this scope's tenant stamp.</summary>
    private sealed class TenantStampingFilterProvider(IJobFilterProvider inner, TenantJobFilter tenantFilter) : IJobFilterProvider
    {
        public IEnumerable<JobFilter> GetFilters(Job job) =>
            inner.GetFilters(job).Append(new JobFilter(tenantFilter, JobFilterScope.Global, null));
    }

    /// <summary>This job server's own extra state and server filters (see <see cref="AddJobServer"/>), on top of the defaults.</summary>
    private sealed class HostScopedFilterProvider(IJobFilterProvider inner, IReadOnlyCollection<object> extraFilters) : IJobFilterProvider
    {
        // Hangfire's own JobFilterProviderCollection sorts by Order after combining providers; a plain Concat here
        // would not, so a low-Order filter (see JobFailureAlertFilter) would run after Hangfire's own Order-20
        // AutomaticRetryAttribute regardless of its declared Order.
        public IEnumerable<JobFilter> GetFilters(Job job) =>
            inner.GetFilters(job).Concat(extraFilters.Select(f => new JobFilter(f, JobFilterScope.Global, null))).OrderBy(f => f.Order);
    }
}
