using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Operations.Contracts;

namespace Platform.IntegrationTests.Operations;

/// <summary>F-60 opening and closing rule against real PostgreSQL, mirroring IncidentRuleTests (Platform.UnitTests).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class HealthLogTests(DatabaseFixture db) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ModuleHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ModuleHost(db.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task A_failure_opens_one_incident_and_repeated_failures_do_not_open_more()
    {
        var component = UniqueComponent();
        await using var scope = _host.ScopeFor(null);
        var log = scope.ServiceProvider.GetRequiredService<IHealthLog>();
        var now = DateTimeOffset.UtcNow;

        var first = await log.RecordAsync([Failure(component, now)], Ct);
        first.ShouldHaveSingleItem();
        first[0].Kind.ShouldBe(IncidentTransitionKind.Opened);

        var second = await log.RecordAsync([Failure(component, now.AddSeconds(1))], Ct);
        second.ShouldBeEmpty();

        var incidents = await log.IncidentsAsync(now.AddMinutes(-1), Ct);
        var mine = incidents.Where(i => i.Component == component).ToList();
        mine.Count.ShouldBe(1);
        mine[0].ClosedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_recovery_closes_the_open_incident()
    {
        var component = UniqueComponent();
        await using var scope = _host.ScopeFor(null);
        var log = scope.ServiceProvider.GetRequiredService<IHealthLog>();
        var now = DateTimeOffset.UtcNow;

        await log.RecordAsync([Failure(component, now)], Ct);
        var transitions = await log.RecordAsync([Healthy(component, now.AddSeconds(1))], Ct);

        transitions.ShouldHaveSingleItem();
        transitions[0].Kind.ShouldBe(IncidentTransitionKind.Closed);

        var incidents = await log.IncidentsAsync(now.AddMinutes(-1), Ct);
        incidents.Single(i => i.Component == component).ClosedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Degraded_does_not_open_an_incident()
    {
        var component = UniqueComponent();
        await using var scope = _host.ScopeFor(null);
        var log = scope.ServiceProvider.GetRequiredService<IHealthLog>();
        var now = DateTimeOffset.UtcNow;

        var transitions = await log.RecordAsync([new HealthResult(component, HealthStatus.Degraded, 10, now)], Ct);

        transitions.ShouldBeEmpty();
        var incidents = await log.IncidentsAsync(now.AddMinutes(-1), Ct);
        incidents.ShouldNotContain(i => i.Component == component);
    }

    [Fact]
    public async Task LatestAsync_returns_the_newest_result_per_component()
    {
        var component = UniqueComponent();
        await using var scope = _host.ScopeFor(null);
        var log = scope.ServiceProvider.GetRequiredService<IHealthLog>();
        var now = DateTimeOffset.UtcNow;

        await log.RecordAsync([Healthy(component, now)], Ct);
        await log.RecordAsync([Failure(component, now.AddSeconds(5))], Ct);

        var latest = await log.LatestAsync(Ct);
        var mine = latest.Single(h => h.Component == component);
        mine.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task A_recovery_keeps_the_failure_reason_in_last_message()
    {
        var component = UniqueComponent();
        await using var scope = _host.ScopeFor(null);
        var log = scope.ServiceProvider.GetRequiredService<IHealthLog>();
        var now = DateTimeOffset.UtcNow;

        await log.RecordAsync([Failure(component, now)], Ct);
        await log.RecordAsync([Healthy(component, now.AddSeconds(1))], Ct);

        var incidents = await log.IncidentsAsync(now.AddMinutes(-1), Ct);
        var closed = incidents.Single(i => i.Component == component);
        closed.ClosedAt.ShouldNotBeNull();
        closed.LastMessage.ShouldBe("connection refused");
    }

    [Fact]
    public async Task Concurrent_failures_on_the_same_component_open_exactly_one_incident_and_keep_both_results()
    {
        var component = UniqueComponent();
        var now = DateTimeOffset.UtcNow;

        // Force the race: a superuser connection locks ops.incidents, so both RecordAsync calls read "no open
        // incident" and then wait on their insert. Without the unique-violation handling in HealthLog, the loser's
        // insert throws once the lock is released and the call never completes (task 3).
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var hold = await owner.BeginTransactionAsync(Ct);
        await using (var lockTable = new NpgsqlCommand("lock table ops.incidents in exclusive mode", owner, hold))
        {
            await lockTable.ExecuteNonQueryAsync(Ct);
        }

        async Task<IReadOnlyList<IncidentTransition>> RecordInOwnScope(DateTimeOffset at)
        {
            await using var scope = _host.ScopeFor(null);
            var log = scope.ServiceProvider.GetRequiredService<IHealthLog>();
            return await log.RecordAsync([Failure(component, at)], Ct);
        }

        var calls = new[] { RecordInOwnScope(now), RecordInOwnScope(now.AddMilliseconds(1)) };
        await WaitUntilBothWaitOrFinishAsync(db.OwnerConnectionString, calls);
        await hold.CommitAsync(Ct);

        // Both calls must complete without throwing; Task.WhenAll rethrows otherwise and fails this test.
        var results = await Task.WhenAll(calls);

        results.SelectMany(r => r).Count(t => t.Kind == IncidentTransitionKind.Opened).ShouldBe(1);

        await using var verifyScope = _host.ScopeFor(null);
        var verifyLog = verifyScope.ServiceProvider.GetRequiredService<IHealthLog>();
        var incidents = (await verifyLog.IncidentsAsync(now.AddMinutes(-1), Ct)).Where(i => i.Component == component).ToList();
        incidents.Count(i => i.ClosedAt is null).ShouldBe(1);

        await using var check = new NpgsqlConnection(db.AppConnectionString);
        await check.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("select count(*) from ops.health_results where component = @c", check);
        count.Parameters.AddWithValue("c", component);
        var storedResults = (long)(await count.ExecuteScalarAsync(Ct))!;
        storedResults.ShouldBe(2);
    }

    [Theory]
    [InlineData("update ops.health_results set message = 'x' where component = 'health-results-privilege-test'")]
    [InlineData("delete from ops.health_results where component = 'health-results-privilege-test'")]
    public async Task Health_results_are_insert_and_select_only_for_the_app_role(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);

        var rejected = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        rejected.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static async Task WaitUntilBothWaitOrFinishAsync(string ownerConnectionString, Task[] tasks)
    {
        // Its own connection: pg_stat_activity is a per-transaction snapshot, so the lock holder cannot poll it.
        await using var owner = new NpgsqlConnection(ownerConnectionString);
        await owner.OpenAsync(Ct);
        await using var waiting = new NpgsqlCommand(
            "select count(*) from pg_stat_activity where datname = current_database() and cardinality(pg_blocking_pids(pid)) > 0", owner);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!tasks.All(t => t.IsCompleted) && (long)(await waiting.ExecuteScalarAsync(Ct))! < tasks.Length)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the two RecordAsync calls neither blocked on ops.incidents nor finished");
            await Task.Delay(50, Ct);
        }
    }

    private static string UniqueComponent() => $"test-component-{Guid.NewGuid():N}";

    private static HealthResult Failure(string component, DateTimeOffset at) =>
        new(component, HealthStatus.Unhealthy, 0, at, "connection refused");

    private static HealthResult Healthy(string component, DateTimeOffset at) =>
        new(component, HealthStatus.Healthy, 5, at);
}
