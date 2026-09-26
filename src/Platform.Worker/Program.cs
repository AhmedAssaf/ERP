using Hangfire;
using Hangfire.AspNetCore;
using Platform.Modules.Audit;
using Platform.Modules.Tenancy;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Shared.Jobs;

namespace Platform.Worker;

// An explicit entry-point class, not top-level statements, for the same reason as Platform.Migrator: a synthesized
// global "Program" would collide with Platform.Web's once a test project references both hosts.
internal static class EntryPoint
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var platformDb = builder.Configuration.GetConnectionString("Platform");
        if (string.IsNullOrWhiteSpace(platformDb))
        {
            throw new InvalidOperationException(
                "Connection string 'Platform' is not configured. Set it with dotnet user-secrets as the erp_app role (docs/07 section 4).");
        }

        builder.Services.AddPlatformShared();
        builder.Services.AddAuditModule(platformDb);
        builder.Services.AddTenancyModule(platformDb);
        builder.Services.AddWorkflowModule(platformDb);
        builder.Services.AddJobServer(platformDb, settings => settings.ServerName = "waslabid-worker");

        using var host = builder.Build();
        // Hangfire logs through a process-wide provider; the worker is the only Hangfire server in its process.
        GlobalConfiguration.Configuration.UseLogProvider(new AspNetCoreLogProvider(host.Services.GetRequiredService<ILoggerFactory>()));
        await host.RunAsync();
    }
}
