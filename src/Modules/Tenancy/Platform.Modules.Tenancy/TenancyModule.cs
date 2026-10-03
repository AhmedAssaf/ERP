using System.Data.Common;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.Modules.Tenancy.Branding;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Jobs;
using Platform.Shared.Data;

namespace Platform.Modules.Tenancy;

public static class TenancyModule
{
    internal const string DataSourceKey = "tenancy";
    private const int DefaultMaxPoolSize = 20;
    private static readonly string[] PoolSizeKeywords = ["Maximum Pool Size", "Max Pool Size", "MaxPoolSize"];

    public static IServiceCollection AddTenancyModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Every module owns a pool; the pools of all modules together must stay below PostgreSQL max_connections.
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var given = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (!PoolSizeKeywords.Any(given.ContainsKey))
        {
            builder.MaxPoolSize = DefaultMaxPoolSize;
        }

        var pooled = builder.ConnectionString;
        // Named (W-10): an unnamed data source is named after its connection string in metrics and spans.
        services.AddKeyedSingleton(DataSourceKey, (_, _) => new NpgsqlDataSourceBuilder(pooled) { Name = DataSourceNames.Tenancy }.Build());
        services.AddSingleton<ITenantDirectory, TenantDirectory>();
        // Scoped: it reads the scope's platform mark (IPlatformRequestContext from AddPlatformShared).
        services.AddScoped<ITenantCatalog, TenantCatalog>();
        // W-10: slugs by id for the worker's usage job, which is not a platform console request (ITenantSlugs).
        services.AddScoped<ITenantSlugs, TenantSlugs>();
        // F-02: needs IAuditWriter (Audit module) and IObjectStorage (AddPlatformShared, configured by AddObjectStorage).
        services.AddScoped<IBrandingService, BrandingService>();
        return services;
    }

    public const string LogoCleanupJobId = "branding-logo-cleanup";

    /// <summary>
    /// The worker's cleanup of unreferenced logo objects (W-38). Needs the platform services, object storage and the job
    /// client of the worker host; <paramref name="configuration"/> may set <c>Branding:LogoCleanup:GracePeriod</c>.
    /// </summary>
    public static IServiceCollection AddBrandingJobs(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<BrandingLogoCleanupOptions>().Bind(configuration.GetSection(BrandingLogoCleanupOptions.Section))
            .Validate(o => o.GracePeriod >= TimeSpan.FromMinutes(1), "Setting 'Branding:LogoCleanup:GracePeriod' must be at least one minute.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<BrandingLogoCleanupJob>();
        return services;
    }

    /// <summary>Schedules the logo cleanup hourly (<see cref="AddBrandingJobs"/>). Call once after the worker host is built.</summary>
    public static void ScheduleBrandingJobs(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.GetRequiredService<RecurringJobCatalog>()
            .AddOrUpdate<BrandingLogoCleanupJob>(LogoCleanupJobId, job => job.RunAsync(CancellationToken.None), Cron.Hourly());
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "tenancy", typeof(TenancyModule).Assembly, cancellationToken);
}
