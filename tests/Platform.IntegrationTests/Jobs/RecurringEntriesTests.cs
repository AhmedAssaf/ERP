using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Operations.Health;
using Platform.Shared.Jobs;

namespace Platform.IntegrationTests.Jobs;

/// <summary>
/// W-42, availability: the recurring entries that schedule the worker's health checks, scans, cleanups and alerts are the
/// worker's. The application role still reads them (the dashboard) but can neither write, delete nor re-time them, nor hold
/// the worker's locks; it keeps what enqueueing and the console's re-run need. The worker's recurring job guard writes back
/// an entry that went missing or was altered anyway (by the owner, a bug, an old version), records the pass as health
/// component "Jobs", which opens one F-60 incident, and closes it on the next intact pass.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RecurringEntriesTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_application_role_reads_but_cannot_write_delete_or_re_time_a_recurring_entry()
    {
        await using var worker = await JobServerHost.StartAsync(db.WorkerConnectionString, cancellationToken: Ct);
        var id = $"w42-rls-{Guid.NewGuid():N}";
        var key = $"recurring-job:{id}";
        worker.Services.GetRequiredService<RecurringJobCatalog>().AddOrUpdate<GuardProbeJob>(id, j => j.RunAsync(), Cron.Yearly());
        try
        {
            await using var app = new NpgsqlConnection(db.AppConnectionString);
            await app.OpenAsync(Ct);

            (await CountAsync(app, "select count(*) from hangfire.hash where key = @key", key)).ShouldBeGreaterThan(0, "the dashboard reads them");
            (await CountAsync(app, "select count(*) from hangfire.set where key = 'recurring-jobs' and value = @key", id)).ShouldBe(1);

            (await RowsAsync(app, "delete from hangfire.hash where key = @key", key)).ShouldBe(0);
            (await RowsAsync(app, "update hangfire.hash set value = '0 0 1 1 *' where key = @key and field = 'Cron'", key)).ShouldBe(0);
            (await RowsAsync(app, "delete from hangfire.set where key = 'recurring-jobs' and value = @key", id)).ShouldBe(0);
            (await RowsAsync(app, "update hangfire.set set score = 4102444800 where key = 'recurring-jobs' and value = @key", id)).ShouldBe(0);
            (await RefusedAsync(app, "insert into hangfire.hash (key, field, value) values (@key || '-forged', 'Cron', '* * * * *')", key)).ShouldBeTrue();
            (await RefusedAsync(app, "insert into hangfire.set (key, value, score) values ('recurring-jobs', @key || '-forged', 0)", id)).ShouldBeTrue();
            // The recurring scheduler's lock, or a job's state lock held into the future, would stall the worker.
            (await RefusedAsync(app, "insert into hangfire.lock (resource, acquired) values ('hangfire:recurring-jobs:lock', now())", key)).ShouldBeTrue();
            (await RefusedAsync(app, "insert into hangfire.lock (resource, acquired) values ('hangfire:job:' || @key || ':state-lock', now() + interval '1 day')", key)).ShouldBeTrue();
            // What enqueueing and the console's re-run take: a job's state lock, now; and other hash and set keys.
            (await RefusedAsync(app, "insert into hangfire.lock (resource, acquired) values ('hangfire:job:' || @key || ':state-lock', now())", key)).ShouldBeFalse();
            (await RefusedAsync(app, "insert into hangfire.set (key, value, score) values ('schedule', @key, 0)", key)).ShouldBeFalse();

            // Hangfire's own path as erp_app: the recurring job's lock is refused (Hangfire.PostgreSql retries it until its
            // 15-second timeout, then gives up), so nothing is written.
            var refused = Should.Throw<Exception>(() => new RecurringJobManager(AppStorage()).AddOrUpdate<GuardProbeJob>(id, j => j.RunAsync(), Cron.Minutely()));
            refused.GetType().Name.ShouldBe("PostgreSqlDistributedLockException");

            using var connection = worker.Storage.GetConnection();
            connection.GetRecurringJobs([id]).Single().Cron.ShouldBe(Cron.Yearly(), "unchanged");
        }
        finally
        {
            worker.RecurringJobs.RemoveIfExists(id);
        }
    }

    [Fact]
    public async Task The_worker_restores_a_deleted_or_altered_recurring_job_and_raises_one_incident_until_an_intact_pass()
    {
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        await using var worker = await GuardedWorkerAsync(TimeSpan.FromHours(1));
        var guard = worker.Services.GetServices<IHostedService>().OfType<RecurringJobGuard>().Single();
        var catalog = worker.Services.GetRequiredService<RecurringJobCatalog>();
        var deleted = $"w42-guard-deleted-{Guid.NewGuid():N}";
        var retimed = $"w42-guard-retimed-{Guid.NewGuid():N}";
        catalog.AddOrUpdate<GuardProbeJob>(deleted, j => j.RunAsync(), Cron.Hourly());
        catalog.AddOrUpdate<GuardProbeJob>(retimed, j => j.RunAsync(), "*/5 * * * *");
        try
        {
            (await guard.RunOnceAsync(Ct)).ShouldBeEmpty("intact");

            // Whatever deleted or changed them (the owner here: the application role no longer can).
            await ExecuteAsOwnerAsync($"""
                delete from hangfire.hash where key = 'recurring-job:{deleted}';
                delete from hangfire.set where key = 'recurring-jobs' and value = '{deleted}';
                update hangfire.hash set value = '0 0 1 1 *' where key = 'recurring-job:{retimed}' and field = 'Cron';
                """);

            (await guard.RunOnceAsync(Ct)).ShouldBe([deleted, retimed], ignoreOrder: true);

            using (var connection = worker.Storage.GetConnection())
            {
                var stored = connection.GetRecurringJobs([deleted, retimed]).ToDictionary(j => j.Id, j => j.Cron);
                stored[deleted].ShouldBe(Cron.Hourly());
                stored[retimed].ShouldBe("*/5 * * * *");
                connection.GetAllItemsFromSet(RecurringJobCatalog.RecurringJobsSet).ShouldContain(deleted);
            }

            // This test's incident only: an earlier test's closed "Jobs" incident may fall inside the window.
            var open = (await IncidentsAsync(worker, since)).Where(i => i.Component == HealthComponents.Jobs && i.ClosedAt is null).ToList();
            open.ShouldHaveSingleItem().LastMessage.ShouldNotBeNull().ShouldContain(deleted);
            open[0].LastMessage!.ShouldContain(retimed);

            (await guard.RunOnceAsync(Ct)).ShouldBeEmpty("restored");
            var after = await IncidentsAsync(worker, since);
            after.Where(i => i.Component == HealthComponents.Jobs && i.ClosedAt is null).ShouldBeEmpty();
            after.Single(i => i.Id == open[0].Id).ClosedAt.ShouldNotBeNull("closed by the intact pass");
        }
        finally
        {
            worker.RecurringJobs.RemoveIfExists(deleted);
            worker.RecurringJobs.RemoveIfExists(retimed);
        }
    }

    [Fact]
    public async Task The_guard_runs_on_its_timer()
    {
        var worker = await GuardedWorkerAsync(TimeSpan.FromMilliseconds(200));
        var id = $"w42-guard-timer-{Guid.NewGuid():N}";
        worker.Services.GetRequiredService<RecurringJobCatalog>().AddOrUpdate<GuardProbeJob>(id, j => j.RunAsync(), Cron.Daily());
        try
        {
            await ExecuteAsOwnerAsync($"delete from hangfire.hash where key = 'recurring-job:{id}'; delete from hangfire.set where key = 'recurring-jobs' and value = '{id}';");

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                using (var connection = worker.Storage.GetConnection())
                {
                    if (connection.GetRecurringJobs([id]).SingleOrDefault() is { Removed: false } restored)
                    {
                        restored.Cron.ShouldBe(Cron.Daily());
                        break;
                    }
                }

                DateTime.UtcNow.ShouldBeLessThan(deadline, "the guard restores the entry on its own");
                await Task.Delay(100, Ct);
            }
        }
        finally
        {
            // An intact pass closes the incident this test's restore opened; then the worker stops (its guard would write the
            // entry back) and the entry is removed, so no later test's job server fires it.
            await worker.Services.GetServices<IHostedService>().OfType<RecurringJobGuard>().Single().RunOnceAsync(Ct);
            await worker.DisposeAsync();
            new RecurringJobManager(WorkerStorage()).RemoveIfExists(id);
        }
    }

    private Task<JobServerHost> GuardedWorkerAsync(TimeSpan interval) =>
        JobServerHost.StartAsync(
            db.WorkerConnectionString,
            services =>
            {
                services.AddOperationsModule(db.WorkerConnectionString);
                services.AddSingleton<IRecurringJobDriftReporter, RecurringJobDriftReporter>();
            },
            configureJobServer: options => options.RecurringJobGuardInterval = interval,
            cancellationToken: Ct);

    private static async Task<IReadOnlyList<Incident>> IncidentsAsync(JobServerHost worker, DateTimeOffset since)
    {
        await using var scope = worker.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IHealthLog>().IncidentsAsync(since, Ct);
    }

    private Hangfire.PostgreSql.PostgreSqlStorage AppStorage() => Storage(db.AppConnectionString);

    private Hangfire.PostgreSql.PostgreSqlStorage WorkerStorage() => Storage(db.WorkerConnectionString);

    private static Hangfire.PostgreSql.PostgreSqlStorage Storage(string connectionString) =>
        new(new Hangfire.PostgreSql.Factories.NpgsqlConnectionFactory(connectionString, new Hangfire.PostgreSql.PostgreSqlStorageOptions()),
            new Hangfire.PostgreSql.PostgreSqlStorageOptions { SchemaName = JobsModule.SchemaName, PrepareSchemaIfNecessary = false });

    private async Task ExecuteAsOwnerAsync(string sql)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
#pragma warning disable CA2100 // The tests' own statements, with generated ids.
        await using var command = new NpgsqlCommand(sql, owner);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, string key)
    {
#pragma warning disable CA2100 // The tests' own statements.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("key", key);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<int> RowsAsync(NpgsqlConnection connection, string sql, string key)
    {
        await using var transaction = await connection.BeginTransactionAsync(Ct);
#pragma warning disable CA2100 // The tests' own statements.
        await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("key", key);
        var rows = await command.ExecuteNonQueryAsync(Ct);
        await transaction.RollbackAsync(Ct);
        return rows;
    }

    /// <summary>True when row-level security refuses the statement; it is rolled back either way.</summary>
    private static async Task<bool> RefusedAsync(NpgsqlConnection connection, string sql, string key)
    {
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        try
        {
#pragma warning disable CA2100 // The tests' own statements.
            await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("key", key);
            await command.ExecuteNonQueryAsync(Ct);
            return false;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            exception.MessageText.ShouldContain("row-level security");
            return true;
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }
}

/// <summary>A recurring platform job for the guard tests; harmless if the scheduler fires it while a test runs.</summary>
[PlatformJob]
public sealed class GuardProbeJob
{
#pragma warning disable CA1822 // An instance job, as the worker's recurring jobs are.
    public Task RunAsync() => Task.CompletedTask;
#pragma warning restore CA1822
}
