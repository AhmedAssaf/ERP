using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;

namespace Platform.IntegrationTests.Operations;

/// <summary>
/// W-10 (spec 6.4): the worker schedules "usage-metrics" every five minutes and "usage-activity-prune" once a day, and
/// Hangfire runs the usage job with the worker's registrations (tenancy for slugs, the Identity module's activity counts,
/// the Operations usage metrics) as a platform job with no tenant. The recurring entries are removed afterwards, so no
/// later test's job server picks them up.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class UsageJobScheduleTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_worker_schedules_the_usage_jobs_and_hangfire_runs_them_without_a_tenant()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, services =>
        {
            services.AddMetrics();
            services.AddTenancyModule(db.WorkerConnectionString);
            services.AddIdentityActivityCounts(db.WorkerConnectionString);
            services.AddOperationsModule(db.WorkerConnectionString);
            services.AddOperationsUsageMetrics();
        }, cancellationToken: Ct);
        var before = DateTimeOffset.UtcNow;
        try
        {
            OperationsModule.ScheduleUsageMetricsJobs(worker.Services);

            using (var connection = worker.Storage.GetConnection())
            {
                var recurring = connection.GetRecurringJobs([OperationsModule.UsageMetricsJobId, OperationsModule.UsageActivityPruneJobId])
                    .ToDictionary(j => j.Id, j => j.Cron);
                recurring[OperationsModule.UsageMetricsJobId].ShouldBe("*/5 * * * *");
                recurring[OperationsModule.UsageActivityPruneJobId].ShouldBe(Cron.Daily());
            }

            var manager = worker.RecurringJobs;
            var metricsRun = manager.TriggerJob(OperationsModule.UsageMetricsJobId);
            var pruneRun = manager.TriggerJob(OperationsModule.UsageActivityPruneJobId);
            await worker.WaitForSuccessAsync(metricsRun.ShouldNotBeNull(), Ct);
            await worker.WaitForSuccessAsync(pruneRun.ShouldNotBeNull(), Ct);

            await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
            await owner.OpenAsync(Ct);
            await using var stored = new NpgsqlCommand("select min(computed_at) from ops.active_user_counts", owner);
            ((DateTime)(await stored.ExecuteScalarAsync(Ct))!).ShouldBeGreaterThanOrEqualTo(before.UtcDateTime.AddSeconds(-1));
        }
        finally
        {
            var manager = worker.RecurringJobs;
            manager.RemoveIfExists(OperationsModule.UsageMetricsJobId);
            manager.RemoveIfExists(OperationsModule.UsageActivityPruneJobId);
        }
    }
}
