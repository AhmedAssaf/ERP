using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Tenancy;

namespace Platform.Shared.Data;

public static class ModuleDbContextRegistration
{
    /// <summary>
    /// Registers a scoped IDbContextFactory for a module context: create one context per operation, never hold one for
    /// the life of a Blazor circuit. Every connection gets the tenant interceptor, which also carries the vendor company and the acting user.
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
