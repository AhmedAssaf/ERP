using Hangfire;
using Hangfire.AspNetCore;
using Platform.Modules.Audit;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;
using Platform.Modules.Vendors;
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
        builder.Services.AddOperationsModule(platformDb);
        builder.Services.AddVendorsModule(platformDb);
        // F-12: the retry scan of pending vendor documents and the cleanup of abandoned uploads (vendor plan task 3).
        builder.Services.AddObjectStorage(builder.Configuration);
        builder.Services.AddVirusScanner(builder.Configuration);
        builder.Services.AddVendorJobs();
        builder.Services.AddOperationsHealthChecks(platformDb, builder.Configuration);
        builder.Services.AddJobServer(platformDb, settings => settings.ServerName = "waslabid-worker");

        using var host = builder.Build();
        // Hangfire logs through a process-wide provider; the worker is the only Hangfire server in its process.
        GlobalConfiguration.Configuration.UseLogProvider(new AspNetCoreLogProvider(host.Services.GetRequiredService<ILoggerFactory>()));
        // F-51: the health-check recurring job (task 3) runs every minute from here on.
        OperationsModule.ScheduleHealthCheckJob(host.Services);
        VendorsModule.ScheduleVendorJobs(host.Services);
        await host.RunAsync();
    }
}
