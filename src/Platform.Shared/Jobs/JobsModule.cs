using Hangfire;
using Hangfire.Client;
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

    /// <summary>
    /// How often the worker checks its recurring jobs and writes back a missing or altered one (W-42,
    /// <see cref="RecurringJobGuard"/>); five minutes by default.
    /// </summary>
    public TimeSpan RecurringJobGuardInterval { get; set; } = TimeSpan.FromMinutes(5);
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

    /// <summary>
    /// Applies the jobs migrations (<c>Jobs/Migrations</c>, journal module <c>jobs</c>) as the owner: they secure Hangfire's own
    /// tables, so the migrator runs them right after <see cref="InstallSchema"/> (W-42).
    /// </summary>
    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection ownerConnection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(ownerConnection, "jobs", typeof(JobsModule).Assembly, "JobsMigrations.", cancellationToken);

    /// <summary>
    /// Registers the storage and a scoped <see cref="IBackgroundJobClient"/> that stamps the current tenant and signs every job
    /// with <paramref name="signingKeys"/> (W-42), plus the signed recurring job manager and the <see cref="RecurringJobCatalog"/>.
    /// </summary>
    public static IServiceCollection AddJobClient(this IServiceCollection services, string connectionString, JobSigningKeys signingKeys) =>
        AddJobClient(services, connectionString, signingKeys, failFast: false);

    /// <summary>
    /// Registers the client plus a Hangfire server whose jobs run in their own DI scope as the enqueuing tenant, each inside
    /// its span and log scope (<see cref="JobTelemetryFilter"/>, W-10), and only if the job is a platform job (<see cref="JobAllowList"/>,
    /// W-36 fix round 1) with a valid signature whose nonce has not run as another job (<see cref="JobGate"/>, W-42). The
    /// server's recurring scheduler signs the jobs it creates, and <see cref="RecurringJobGuard"/> keeps the recurring entries
    /// in place.
    /// </summary>
    public static IServiceCollection AddJobServer(
        this IServiceCollection services, string connectionString, JobSigningKeys signingKeys, Action<JobServerSettings>? configure = null)
    {
        // W-36 fix rounds 1 and 2: Hangfire's type resolver is process-wide; a process that runs a job server (the worker,
        // and the integration tests' in-process workers) resolves only the allow-listed types, for job rows and for the
        // $type bindings of Hangfire's internal serializer. Set here, at registration, so it is in place before anything
        // reads a stored row, the worker's recurring-job scheduling after the host is built included.
        GlobalConfiguration.Configuration.UseTypeResolver(JobAllowList.ResolveType);
        AddJobClient(services, connectionString, signingKeys, failFast: true);
        var settings = new JobServerSettings();
        configure?.Invoke(settings);
        services.AddSingleton(settings);
        // W-42: the nonce of a signed job runs under one job id only; the ledger is the worker role's alone.
        services.TryAddSingleton(sp => new JobReplayLedger(sp.GetRequiredKeyedService<NpgsqlDataSource>(DataSourceKey)));
        services.TryAddSingleton(sp => new JobGate(sp.GetRequiredService<JobAuthenticity>(), sp.GetRequiredService<JobReplayLedger>()));
        services.AddHostedService<RecurringJobGuard>();
        services.AddSingleton<IServerFilter, JobTelemetryFilter>();
        // W-36 fix round 1: a job row is untrusted (the application role can write Hangfire's tables); only platform jobs run.
        services.AddSingleton<JobAllowListFilter>();
        services.AddSingleton<IServerFilter>(sp => sp.GetRequiredService<JobAllowListFilter>());
        services.AddSingleton<IElectStateFilter>(sp => sp.GetRequiredService<JobAllowListFilter>());

        services.AddSingleton<IHostedService>(sp =>
        {
            // This host's own DI-registered state and server filters (e.g. Operations' job-failure alert, the job telemetry),
            // on top of Hangfire's process-wide defaults. Never Hangfire's static GlobalJobFilters.Filters: several Hangfire
            // servers can share one process (tests build one per test host), and a filter registered for one must not run
            // on another's jobs. An instance registered under two of these interfaces is still one filter.
            // W-42: and the signing filter, so the jobs the server's recurring scheduler creates are signed like the client's.
            var hostFilters = sp.GetServices<IElectStateFilter>().Cast<object>()
                .Concat(sp.GetServices<IApplyStateFilter>())
                .Concat(sp.GetServices<IServerFilter>())
                .Append(sp.GetRequiredService<JobSigningFilter>())
                .Distinct(ReferenceEqualityComparer.Instance)
                .ToArray();

            var options = new BackgroundJobServerOptions
            {
                Activator = new TenantJobActivator(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<JobGate>()),
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

    private static IServiceCollection AddJobClient(IServiceCollection services, string connectionString, JobSigningKeys signingKeys, bool failFast)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(signingKeys);

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
        // W-42: every job this host creates is signed (JobSigningFilter, last among the client filters).
        services.TryAddSingleton(sp => new JobAuthenticity(signingKeys, sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton(sp => new JobSigningFilter(sp.GetRequiredService<JobAuthenticity>()));
        services.TryAddScoped<IBackgroundJobClient>(sp => new BackgroundJobClient(
            sp.GetRequiredService<JobStorage>(),
            new ClientFilterProvider(
                JobFilterProviders.Providers,
                [new TenantJobFilter(sp.GetRequiredService<ITenantAccessor>()), sp.GetRequiredService<JobSigningFilter>()])));
        // The recurring job manager writes the recurring entries (only the worker's role may, jobs migration 0001) and signs
        // what it triggers; RecurringJobCatalog remembers the definitions for the worker's guard.
        services.TryAddSingleton(sp => new RecurringJobManager(
            sp.GetRequiredService<JobStorage>(), new ClientFilterProvider(JobFilterProviders.Providers, [sp.GetRequiredService<JobSigningFilter>()])));
        services.TryAddSingleton(sp => new RecurringJobCatalog(sp.GetRequiredService<JobStorage>(), sp.GetRequiredService<RecurringJobManager>()));
        return services;
    }

    /// <summary>Hangfire's connections from the named data source, never from a bare connection string.</summary>
    private sealed class DataSourceConnectionFactory(NpgsqlDataSource dataSource) : IConnectionFactory
    {
        public NpgsqlConnection GetOrCreateConnection() => dataSource.CreateConnection();
    }

    /// <summary>
    /// The global filters (retries, culture, continuations) plus this host's client filters, in the given order after them:
    /// the scope's tenant stamp, then the signature (W-42), which must see the stamp.
    /// </summary>
    private sealed class ClientFilterProvider(IJobFilterProvider inner, IReadOnlyList<IClientFilter> filters) : IJobFilterProvider
    {
        public IEnumerable<JobFilter> GetFilters(Job job) =>
            inner.GetFilters(job).Concat(filters.Select(f => new JobFilter(f, JobFilterScope.Global, null)));
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
