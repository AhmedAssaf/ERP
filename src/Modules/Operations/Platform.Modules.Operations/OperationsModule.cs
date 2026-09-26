using Hangfire;
using Hangfire.States;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Platform.Modules.Operations.Alerts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Operations.PlatformConsole;
using Platform.Modules.Operations.Health;
using Platform.Shared.Data;

namespace Platform.Modules.Operations;

public static class OperationsModule
{
    /// <summary>The recurring job id used by <see cref="ScheduleHealthCheckJob"/> (plan task 3).</summary>
    public const string HealthCheckJobId = "health-check";

    public static IServiceCollection AddOperationsModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddModuleDbContext<OperationsDbContext>(connectionString);
        services.AddScoped<IHealthLog, HealthLog>();
        services.AddScoped<IPlatformAudit, PlatformAuditWriter>();
        return services;
    }

    /// <summary>
    /// Registers what only the platform console needs (plan task 7, spec 3.4): failed jobs with the audited re-run, and
    /// tenant storage usage. The host must also register Hangfire's storage (<c>JobsModule.AddJobClient</c>). Object
    /// storage settings (<c>ObjectStorage:*</c>) are optional; without them storage usage is unknown.
    /// </summary>
    public static IServiceCollection AddOperationsConsole(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(_ => ObjectStorageSettings.FromConfiguration(configuration));
        services.AddSingleton<ITenantStorageUsage, TenantStorageUsage>();
        services.AddScoped<IPlatformJobs, PlatformJobs>();
        return services;
    }

    /// <summary>
    /// Registers the health checks (spec 3.2) and the job that runs them; only the worker needs this (D-7: the
    /// checks run from a Hangfire recurring job in the worker, never probed live by the board). Includes the alert
    /// wiring (<see cref="AddOperationsAlerts"/>) so <see cref="Health.HealthCheckJob"/> can notify on every
    /// incident, plus a disk-usage check (docs/05 row 19) that goes through the same incident pipeline rather than
    /// the F-51 board's fixed seven tiles (docs/05 row 17).
    /// </summary>
    public static IServiceCollection AddOperationsHealthChecks(
        this IServiceCollection services, string postgreSqlConnectionString, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(postgreSqlConnectionString);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOperationsAlerts(configuration);
        services.AddHttpClient();
        services.AddSingleton(_ => HealthCheckSettings.FromConfiguration(configuration, postgreSqlConnectionString));

        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            return new NamedHealthCheck(HealthComponents.PostgreSql, new PostgreSqlHealthCheck(settings.PostgreSqlConnectionString));
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            return new NamedHealthCheck(
                HealthComponents.ObjectStorage, new MinIoHealthCheck(settings.MinIoServiceUrl, settings.MinIoBucketName, settings.MinIoAccessKey, settings.MinIoSecretKey));
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KeycloakHealthCheck));
            return new NamedHealthCheck(HealthComponents.Keycloak, new KeycloakHealthCheck(httpClient, settings.KeycloakManagementUrl));
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            return new NamedHealthCheck(HealthComponents.ClamAv, new ClamAvHealthCheck(settings.ClamAvHost, settings.ClamAvPort));
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            return new NamedHealthCheck(HealthComponents.Email, new SmtpHealthCheck(settings.SmtpHost, settings.SmtpPort));
        });
        services.AddSingleton(sp =>
            new NamedHealthCheck(HealthComponents.Worker, new WorkerHeartbeatHealthCheck(sp.GetRequiredService<JobStorage>(), sp.GetRequiredService<TimeProvider>())));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(WebHealthCheck));
            return new NamedHealthCheck(HealthComponents.Web, new WebHealthCheck(httpClient, settings.WebHealthUrl));
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            var env = sp.GetRequiredService<IHostEnvironment>();
            return new NamedHealthCheck(
                HealthComponents.Disk, new DiskSpaceHealthCheck(settings.DiskPathOr(env.ContentRootPath), settings.DiskAlertPercent));
        });

        // One per worker process: remembers what was alerted while the health store (PostgreSQL) is unavailable.
        services.AddSingleton<FallbackAlertState>();
        services.AddScoped<HealthCheckJob>();
        return services;
    }

    /// <summary>
    /// Registers the alert email path (plan task 4, F-60 as narrowed): <see cref="IAlertSender"/> (MailKit, D-13),
    /// <see cref="IncidentNotifier"/> (consumed by <see cref="Health.HealthCheckJob"/>), and the Hangfire job-failure
    /// filter. Split out from <see cref="AddOperationsHealthChecks"/> so a caller that only needs the job-failure
    /// alert (a Hangfire server with no health checks of its own) is not forced to configure every check's settings.
    /// </summary>
    public static IServiceCollection AddOperationsAlerts(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(_ => AlertSettings.FromConfiguration(configuration));
        services.AddSingleton<IAlertSender, MailKitAlertSender>();
        services.AddScoped<IncidentNotifier>();
        // A per-job-server filter (Platform.Shared.Jobs.JobsModule.AddJobServer picks up IElectStateFilter /
        // IApplyStateFilter registrations from this same container), never Hangfire's static GlobalJobFilters.
        services.AddSingleton<IElectStateFilter, JobFailureAlertFilter>();
        return services;
    }

    /// <summary>Schedules the "health-check" recurring job to run every minute (D-7). Call once after the host is built.</summary>
    public static void ScheduleHealthCheckJob(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var storage = services.GetRequiredService<JobStorage>();
        new RecurringJobManager(storage).AddOrUpdate<HealthCheckJob>(
            HealthCheckJobId, job => job.RunAsync(CancellationToken.None), Cron.Minutely());
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "operations", typeof(OperationsModule).Assembly, cancellationToken);
}
