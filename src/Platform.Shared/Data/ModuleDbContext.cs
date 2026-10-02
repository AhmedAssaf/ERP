using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Tenancy;

namespace Platform.Shared.Data;

public static class ModuleDbContextRegistration
{
    /// <summary>
    /// Registers a scoped IDbContextFactory for a module context: create one context per operation, never hold one for
    /// the life of a Blazor circuit. Every connection gets the tenant interceptor, which also carries the vendor company and the acting user.
    /// The contexts open their connections from the connection string, so they share Npgsql's pool for it with any plain
    /// connection on the same string (a row-level security test relies on that). That pool has no name of its own: Npgsql
    /// names it after the connection string, which never leaves the process (W-10 final fix wave: the metric view in
    /// <c>TelemetryModule</c> drops the pool name, the span processor drops <c>db.npgsql.data_source</c>).
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string connectionString)
        where TContext : DbContext
    {
        services.AddDbContextFactory<TContext>(
            (provider, options) => options
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new TenantConnectionInterceptor(
                    provider.GetRequiredService<ITenantAccessor>(),
                    provider.GetRequiredService<IVendorAccessor>(),
                    provider.GetRequiredService<IActingUserAccessor>())),
            ServiceLifetime.Scoped);
        return services;
    }
}
