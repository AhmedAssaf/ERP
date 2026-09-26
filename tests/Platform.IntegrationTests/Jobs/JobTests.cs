using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Jobs;

/// <summary>W-08: the worker runs Hangfire on PostgreSQL, each job once, as the tenant that enqueued it.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class JobTests(DatabaseFixture db) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            create schema if not exists jobs_test;
            create table if not exists jobs_test.counters (id uuid primary key, value int not null);
            grant usage on schema jobs_test to erp_app;
            grant select, insert, update on jobs_test.counters to erp_app;
            """, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_job_runs_once_with_two_worker_instances()
    {
        void Counters(IServiceCollection services) => services.AddSingleton(new CounterTable(db.AppConnectionString));
        await using var first = await JobServerHost.StartAsync(db.AppConnectionString, Counters, Ct);
        await using var second = await JobServerHost.StartAsync(db.AppConnectionString, Counters, Ct);
        await WaitUntilAsync(() => first.ServerIsRegistered() && second.ServerIsRegistered());

        var counters = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var jobIds = new List<string>();
        await using (var scope = first.ScopeFor(null))
        {
            var client = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            jobIds.AddRange(counters.Select(id => client.Enqueue<CounterJob>(job => job.IncrementAsync(id))));
        }

        foreach (var jobId in jobIds)
        {
            await first.WaitForSuccessAsync(jobId, Ct);
        }

        // Give a duplicate pickup time to show up before counting.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        var values = await new CounterTable(db.AppConnectionString).ReadAsync(counters, Ct);
        values.Count.ShouldBe(counters.Count);
        values.Values.ShouldAllBe(v => v == 1);
    }

    [Fact]
    public async Task A_job_runs_as_the_tenant_that_enqueued_it()
    {
        var results = new ProbeResults();
        await using var worker = await JobServerHost.StartAsync(db.AppConnectionString, s => s.AddSingleton(results), Ct);
        var probeId = Guid.NewGuid();

        string jobId;
        await using (var scope = worker.ScopeFor(TestTenants.Acme))
        {
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>()
                .Enqueue<TenantProbeJob>(job => job.RecordAndAuditAsync(probeId));
        }

        await worker.WaitForSuccessAsync(jobId, Ct);

        results.Seen[probeId].ShouldBe(TestTenants.Acme);
        SerializationHelper.Deserialize<Guid>(worker.Storage.GetParameter(jobId, "TenantId")).ShouldBe(TestTenants.Acme.TenantId);
        (await AuditTenantsAsync(probeId)).ShouldBe([TestTenants.Acme.TenantId]);
    }

    [Fact]
    public async Task A_job_enqueued_without_a_tenant_runs_without_one()
    {
        var results = new ProbeResults();
        await using var worker = await JobServerHost.StartAsync(db.AppConnectionString, s => s.AddSingleton(results), Ct);
        var probeId = Guid.NewGuid();

        string jobId;
        await using (var scope = worker.ScopeFor(null))
        {
            jobId = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>()
                .Enqueue<TenantProbeJob>(job => job.Record(probeId));
        }

        await worker.WaitForSuccessAsync(jobId, Ct);

        results.Seen.ShouldContainKey(probeId);
        results.Seen[probeId].ShouldBeNull();
        worker.Storage.GetParameter(jobId, "TenantId").ShouldBeNull();
    }

    [Fact]
    public async Task The_hangfire_schema_is_prepared_by_the_app_role()
    {
        await using var worker = await JobServerHost.StartAsync(db.AppConnectionString, cancellationToken: Ct);
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select distinct tableowner from pg_tables where schemaname = 'hangfire'", connection);

        var owners = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            owners.Add(reader.GetString(0));
        }

        owners.ShouldBe(["erp_app"]);
    }

    [Fact]
    public async Task The_app_role_may_create_objects_only_in_the_hangfire_schema()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select n.nspname
            from pg_namespace n
            where has_schema_privilege('erp_app', n.oid, 'CREATE')
            union all
            select 'database ' || current_database() where has_database_privilege('erp_app', current_database(), 'CREATE')
            """, connection);

        var creatable = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            creatable.Add(reader.GetString(0));
        }

        creatable.ShouldBe(["hangfire"]);
    }

    private async Task<List<Guid>> AuditTenantsAsync(Guid probeId)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select tenant_id from audit.events where action = 'jobs.probe' and subject_id = @subject", connection);
        command.Parameters.AddWithValue("subject", probeId.ToString());
        var tenants = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            tenants.Add(reader.GetGuid(0));
        }

        return tenants;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true within 30 seconds.");
            }

            await Task.Delay(100, Ct);
        }
    }
}

public sealed class CounterTable(string connectionString)
{
    public async Task IncrementAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            insert into jobs_test.counters (id, value) values (@id, 1)
            on conflict (id) do update set value = jobs_test.counters.value + 1
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<Guid, int>> ReadAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select id, value from jobs_test.counters where id = any(@ids)", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        var values = new Dictionary<Guid, int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetGuid(0)] = reader.GetInt32(1);
        }

        return values;
    }
}

public sealed class CounterJob(CounterTable counters)
{
    public async Task IncrementAsync(Guid counterId)
    {
        await counters.IncrementAsync(counterId);
        // Long enough that both servers hold jobs at the same time.
        await Task.Delay(50);
    }
}

public sealed class ProbeResults
{
    public ConcurrentDictionary<Guid, TenantContext?> Seen { get; } = new();
}

public sealed class TenantProbeJob(ITenantAccessor tenants, IAuditWriter audit, ProbeResults results)
{
    public void Record(Guid probeId) => results.Seen[probeId] = tenants.Current;

    public async Task RecordAndAuditAsync(Guid probeId)
    {
        Record(probeId);
        await audit.WriteAsync(new AuditEntry("job-test", "jobs.probe", "probe", probeId.ToString()));
    }
}
