using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Operations.Contracts;
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
    /// Registers the health checks (spec 3.2) and the job that runs them; only the worker needs this (D-7: the
    /// checks run from a Hangfire recurring job in the worker, never probed live by the board).
    /// </summary>
    public static IServiceCollection AddOperationsHealthChecks(
        this IServiceCollection services, string postgreSqlConnectionString, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(postgreSqlConnectionString);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpClient();
        services.AddSingleton(_ => HealthCheckSettings.FromConfiguration(configuration, postgreSqlConnectionString));

        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            return new NamedHealthCheck("PostgreSQL", new PostgreSqlHealthCheck(settings.PostgreSqlConnectionString));
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            return new NamedHealthCheck(
                "MinIO", new MinIoHealthCheck(settings.MinIoServiceUrl, settings.MinIoBucketName, settings.MinIoAccessKey, settings.MinIoSecretKey));
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KeycloakHealthCheck));
            return new NamedHealthCheck("Keycloak", new KeycloakHealthCheck(httpClient, settings.KeycloakManagementUrl));
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            return new NamedHealthCheck("ClamAV", new ClamAvHealthCheck(settings.ClamAvHost, settings.ClamAvPort));
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            return new NamedHealthCheck("SMTP", new SmtpHealthCheck(settings.SmtpHost, settings.SmtpPort));
        });
        services.AddSingleton(sp =>
            new NamedHealthCheck("Worker", new WorkerHeartbeatHealthCheck(sp.GetRequiredService<JobStorage>(), sp.GetRequiredService<TimeProvider>())));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<HealthCheckSettings>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(WebHealthCheck));
            return new NamedHealthCheck("Web", new WebHealthCheck(httpClient, settings.WebHealthUrl));
        });

        services.AddScoped<HealthCheckJob>();
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
