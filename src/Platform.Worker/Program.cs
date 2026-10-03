using Hangfire;
using Hangfire.AspNetCore;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;
using Platform.Modules.Vendors;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Shared.Data;
using Platform.Shared.Jobs;
using Platform.Shared.Telemetry;

namespace Platform.Worker;

// An explicit entry-point class, not top-level statements, for the same reason as Platform.Migrator: a synthesized
// global "Program" would collide with Platform.Web's once a test project references both hosts.
internal static class EntryPoint
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // W-36 (ADR-0012 addendum 2026-10-03): the worker connects only as its own role, erp_worker, from
        // ConnectionStrings:Worker, in every environment: the worker-only functions and the ops writes are that role's rights,
        // so as erp_app its jobs would be refused. Refused at start when missing or for any other role.
        var platformDb = WorkerDatabase.ConnectionString(builder.Configuration);

        // W-10 (spec O-3 to O-6, O-16): job and health spans, metrics and logs over OTLP only when Telemetry:OtlpEndpoint is
        // set; outside Development and Testing no console output.
        builder.AddPlatformTelemetry(TelemetryNames.Services.Worker);
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
        // W-38: removes logo objects no tenant references (needs the object storage just above).
        builder.Services.AddBrandingJobs(builder.Configuration);
        builder.Services.AddOperationsHealthChecks(platformDb, builder.Configuration);
        // W-33: tell the platform admins about new CR ownership disputes (needs the F-60 alerts registered just above).
        builder.Services.AddVendorDisputeAlerts();
        // W-10 business metrics (spec 6.4): the usage job counts the active users every five minutes, for the console usage
        // page (ops.active_user_counts) and the gauges on meter WaslaBid.Usage.
        builder.Services.AddIdentityActivityCounts(platformDb);
        builder.Services.AddOperationsUsageMetrics();
        builder.Services.AddJobServer(platformDb, settings => settings.ServerName = "waslabid-worker");

        using var host = builder.Build();
        // Hangfire logs through a process-wide provider; the worker is the only Hangfire server in its process.
        GlobalConfiguration.Configuration.UseLogProvider(new AspNetCoreLogProvider(host.Services.GetRequiredService<ILoggerFactory>()));
        // F-51: the health-check recurring job (task 3) runs every minute from here on.
        OperationsModule.ScheduleHealthCheckJob(host.Services);
        OperationsModule.ScheduleUsageMetricsJobs(host.Services);
        VendorsModule.ScheduleVendorJobs(host.Services);
        TenancyModule.ScheduleBrandingJobs(host.Services);
        await host.RunAsync();
    }
}
