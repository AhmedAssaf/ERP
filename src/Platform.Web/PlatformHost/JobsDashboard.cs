using Hangfire;
using Hangfire.Dashboard;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Platform.Web.PlatformHost;

/// <summary>
/// Hangfire's dashboard for platform admins at <c>/platform/jobs</c> (plan task 7). Read-only: a re-run goes through
/// the console's tenant page, which confirms it and writes <c>ops.platform_audit</c> (D-8); the dashboard would not.
/// It never shows the storage connection string (N-10).
/// </summary>
internal static class JobsDashboard
{
    public const string Path = "/platform/jobs";

    /// <summary>
    /// The two services <c>MapHangfireDashboard</c> looks for. Not <c>AddHangfire</c>: its configuration callback sets
    /// Hangfire's process-wide log provider, activator and filters from this host's container (JobsModule's rule: no
    /// static configuration; the web host runs no jobs, and a second host in the same process would inherit them).
    /// </summary>
    public static IServiceCollection AddJobsDashboard(this IServiceCollection services)
    {
        services.TryAddSingleton<IGlobalConfiguration>(GlobalConfiguration.Configuration);
        services.TryAddSingleton(DashboardRoutes.Routes);
        return services;
    }

    public static IEndpointConventionBuilder MapJobsDashboard(this WebApplication app)
    {
        var options = new DashboardOptions
        {
            AppPath = "/platform",
            Authorization = [],
            AsyncAuthorization = [new JobsDashboardAuthorizationFilter()],
            IsReadOnlyFunc = static _ => true,
            DisplayStorageConnectionString = false,
        };

        return app.MapHangfireDashboard(Path, options, app.Services.GetRequiredService<JobStorage>())
            .RequireAuthorization(PlatformAuthentication.PolicyName);
    }
}
