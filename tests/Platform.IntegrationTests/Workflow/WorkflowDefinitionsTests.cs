using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Workflow.Contracts;
using Platform.Modules.Workflow.Persistence;
using Platform.Shared.Results;

namespace Platform.IntegrationTests.Workflow;

[Collection(DatabaseCollection.Name)]
public sealed class WorkflowDefinitionsTests(DatabaseFixture db) : IAsyncLifetime
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
    public async Task Saving_stores_the_steps_in_order()
    {
        await using var scope = _host.ScopeFor(TestTenants.Beta);
        var id = (await Definitions(scope).SaveAsync(new SaveDefinition(null, "Chain A", false, DefaultTemplate.Steps), Ct)).Value;

        await using var context = await Context(scope);
        var stages = await context.DefinitionSteps.Where(s => s.DefinitionId == id).OrderBy(s => s.Position).Select(s => s.Stage).ToListAsync(Ct);

        stages.ShouldBe(DefaultTemplate.Steps.Select(s => s.Stage));
    }

    [Fact]
    public async Task Saving_again_increments_the_version_and_replaces_the_steps()
    {
        await using var scope = _host.ScopeFor(TestTenants.Beta);
        var definitions = Definitions(scope);
        var id = (await definitions.SaveAsync(new SaveDefinition(null, "Chain B", false, DefaultTemplate.Steps), Ct)).Value;
        List<StepDefinition> edited = [DefaultTemplate.Steps[0], .. DefaultTemplate.Steps.Skip(2)];

        var again = await definitions.SaveAsync(new SaveDefinition(id, "Chain B", false, edited), Ct);

        again.Value.ShouldBe(id);
        await using var context = await Context(scope);
        var row = await context.Definitions.Include(d => d.Steps).SingleAsync(d => d.Id == id, Ct);
        row.Version.ShouldBe(2);
        row.Steps.Count.ShouldBe(edited.Count);
    }

    [Fact]
    public async Task A_tenant_has_at_most_one_default()
    {
        await using var scope = _host.ScopeFor(TestTenants.Beta);
        var definitions = Definitions(scope);
        await definitions.SaveAsync(new SaveDefinition(null, "Default 1", true, DefaultTemplate.Steps), Ct);
        var second = (await definitions.SaveAsync(new SaveDefinition(null, "Default 2", true, DefaultTemplate.Steps), Ct)).Value;

        (await definitions.FindDefaultAsync(Ct)).ShouldBe(second);
        await using var context = await Context(scope);
        (await context.Definitions.CountAsync(d => d.IsDefault, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_definition_with_financial_opening_before_locking_is_rejected_with_the_rule_named()
    {
        await using var scope = _host.ScopeFor(TestTenants.Beta);
        List<StepDefinition> broken = [DefaultTemplate.Steps[0], DefaultTemplate.Steps[1], DefaultTemplate.Steps[3], DefaultTemplate.Steps[2], DefaultTemplate.Steps[4]];

        var result = await Definitions(scope).SaveAsync(new SaveDefinition(null, "Broken", false, broken), Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Message.ShouldContain("Financial opening must follow score locking (F-30).");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_definition_without_a_name_is_rejected(string name)
    {
        await using var scope = _host.ScopeFor(TestTenants.Beta);

        var result = await Definitions(scope).SaveAsync(new SaveDefinition(null, name, false, DefaultTemplate.Steps), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Code.ShouldBe("workflow.name_missing");
    }

    [Fact]
    public async Task A_default_racing_another_default_is_a_conflict_not_an_exception()
    {
        // Another save holds an uncommitted default for the tenant: our save cannot see it, clears nothing, and then
        // blocks on the one-default unique index until the other save commits.
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        var otherId = Guid.CreateVersion7();
        await using (var clear = new NpgsqlCommand("update workflow.workflow_definition set is_default = false where tenant_id = @tenant", owner))
        {
            clear.Parameters.AddWithValue("tenant", TestTenants.Beta.TenantId);
            await clear.ExecuteNonQueryAsync(Ct);
        }

        await using var other = await owner.BeginTransactionAsync(Ct);
        await using (var insert = new NpgsqlCommand(
            "insert into workflow.workflow_definition (id, tenant_id, name, version, is_default, created_at) values (@id, @tenant, 'Other', 1, true, now())",
            owner,
            other))
        {
            insert.Parameters.AddWithValue("id", otherId);
            insert.Parameters.AddWithValue("tenant", TestTenants.Beta.TenantId);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        async Task<Result<Guid>> SaveInOwnScope()
        {
            await using var scope = _host.ScopeFor(TestTenants.Beta);
            return await Definitions(scope).SaveAsync(new SaveDefinition(null, "Racing default", true, DefaultTemplate.Steps), Ct);
        }

        var save = SaveInOwnScope();
        await WaitUntilBlockedOrFinishedAsync(save);
        await other.CommitAsync(Ct);
        var result = await save;

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        await using (var cleanup = new NpgsqlCommand("delete from workflow.workflow_definition where id = @id", owner))
        {
            cleanup.Parameters.AddWithValue("id", otherId);
            await cleanup.ExecuteNonQueryAsync(Ct);
        }
    }

    private async Task WaitUntilBlockedOrFinishedAsync(Task save)
    {
        await using var watcher = new NpgsqlConnection(db.OwnerConnectionString);
        await watcher.OpenAsync(Ct);
        await using var waiting = new NpgsqlCommand(
            "select count(*) from pg_stat_activity where datname = current_database() and cardinality(pg_blocking_pids(pid)) > 0", watcher);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!save.IsCompleted && (long)(await waiting.ExecuteScalarAsync(Ct))! < 1)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the save neither blocked on the default index nor finished");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_definition()
    {
        Guid id;
        await using (var beta = _host.ScopeFor(TestTenants.Beta))
        {
            id = (await Definitions(beta).SaveAsync(new SaveDefinition(null, "Private", false, DefaultTemplate.Steps), Ct)).Value;
        }

        await using var acme = _host.ScopeFor(TestTenants.Acme);
        await using var context = await Context(acme);
        (await context.Definitions.IgnoreQueryFilters().AnyAsync(d => d.Id == id, Ct)).ShouldBeFalse();
    }

    private static IWorkflowDefinitions Definitions(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IWorkflowDefinitions>();

    private static Task<WorkflowDbContext> Context(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorkflowDbContext>>().CreateDbContextAsync(Ct);
}
