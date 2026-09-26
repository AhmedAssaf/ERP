using Microsoft.Extensions.DependencyInjection;
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

    private static string UniqueComponent() => $"test-component-{Guid.NewGuid():N}";

    private static HealthResult Failure(string component, DateTimeOffset at) =>
        new(component, HealthStatus.Unhealthy, 0, at, "connection refused");

    private static HealthResult Healthy(string component, DateTimeOffset at) =>
        new(component, HealthStatus.Healthy, 5, at);
}
