using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit;
using Platform.Modules.Workflow.Contracts;
using Platform.Modules.Workflow.Persistence;
using Platform.Shared.Results;

namespace Platform.IntegrationTests.Workflow;

/// <summary>F-56 acceptance, run against real PostgreSQL through the module's public contracts.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class WorkflowExecutorTests(DatabaseFixture db) : IAsyncLifetime
{
    private static readonly Dictionary<string, IReadOnlyList<string>> Committee = new()
    {
        ["contracts"] = ["u.legal"],
        ["evaluator"] = ["u.tech1", "u.tech2"],
        ["finance"] = ["u.cfo"],
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ModuleHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ModuleHost(db.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Publishing_snapshots_the_definition_and_the_committee_fills_the_roles()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var (tenderId, _) = await StartAsync(scope);

        var status = (await Service(scope).GetStatusAsync(tenderId, Ct)).Value;

        status.CurrentStage.ShouldBe(Stage.Screening);
        status.PendingUsers.ShouldBe(["u.legal"]);
        status.Steps[1].AssignedUsers.ShouldBe(["u.tech1", "u.tech2"]);
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorkflowDbContext>>().CreateDbContextAsync(Ct);
        var snapshot = await context.TenderWorkflows.Where(w => w.TenderId == tenderId).Select(w => w.Snapshot).SingleAsync(Ct);
        snapshot.ShouldContain("Technical committee");
    }

    [Fact]
    public async Task Editing_the_definition_afterwards_leaves_the_running_tender_unchanged()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var (tenderId, definitionId) = await StartAsync(scope);

        List<StepDefinition> edited =
        [
            DefaultTemplate.Steps[0],
            new(Stage.TechnicalScoring, "Senior committee", StepRule.AnyOf, ["senior-evaluator"]),
            new(Stage.TechnicalScoring, "Procurement head", StepRule.AnyOf, ["head"]),
            .. DefaultTemplate.Steps.Skip(2),
        ];
        (await Definitions(scope).SaveAsync(new SaveDefinition(definitionId, "Edited", false, edited), Ct)).IsSuccess.ShouldBeTrue();

        var status = (await Service(scope).GetStatusAsync(tenderId, Ct)).Value;
        status.Steps.Count.ShouldBe(DefaultTemplate.Steps.Count);
        status.Steps[1].Department.ShouldBe("Technical committee");
        status.Steps[1].AssignedUsers.ShouldBe(["u.tech1", "u.tech2"]);
    }

    [Fact]
    public async Task An_all_of_step_stays_open_until_every_approver_decides_and_names_who_is_pending()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var service = Service(scope);
        var (tenderId, _) = await StartAsync(scope);
        await service.DecideAsync(tenderId, "u.legal", Decision.Approve, Ct);

        var afterOne = (await service.DecideAsync(tenderId, "u.tech1", Decision.Approve, Ct)).Value;

        afterOne.CurrentStage.ShouldBe(Stage.TechnicalScoring);
        afterOne.PendingUsers.ShouldBe(["u.tech2"]);
        var decided = (await AuditRowsAsync(scope, tenderId, "workflow.decided")).Single(e => e.ActorId == "u.tech1");
        decided["pending"].ShouldBe("u.tech2");

        var afterBoth = (await service.DecideAsync(tenderId, "u.tech2", Decision.Approve, Ct)).Value;
        afterBoth.CurrentStage.ShouldBe(Stage.LockScores);
    }

    [Fact]
    public async Task A_decision_from_an_unassigned_user_is_refused_audited_and_changes_nothing()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var service = Service(scope);
        var (tenderId, _) = await StartAsync(scope);

        var result = await service.DecideAsync(tenderId, "u.intruder", Decision.Approve, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(ErrorKind.Refused);
        (await service.GetStatusAsync(tenderId, Ct)).Value.PendingUsers.ShouldBe(["u.legal"]);
        var refused = (await AuditRowsAsync(scope, tenderId, "workflow.refused")).ShouldHaveSingleItem();
        refused.ActorId.ShouldBe("u.intruder");
        refused["reason"].ShouldBe("workflow.not_assigned");
        refused["step"].ShouldBe("1");
    }

    [Fact]
    public async Task Financial_opening_while_at_screening_is_refused_with_the_F30_message_and_audited()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var (tenderId, _) = await StartAsync(scope);

        var result = await Service(scope).CompleteSystemStageAsync(tenderId, Stage.FinancialOpening, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.InvariantViolated);
        result.Error.Message.ShouldBe("Financial opening must follow score locking (F-30).");
        var refused = (await AuditRowsAsync(scope, tenderId, "workflow.refused")).ShouldHaveSingleItem();
        refused.ActorId.ShouldBeNull();
        refused["reason"].ShouldBe("workflow.stage_not_current");
        refused["stage"].ShouldBe("FinancialOpening");
    }

    [Fact]
    public async Task A_second_decision_by_the_same_user_is_a_conflict_and_adds_no_decision()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var service = Service(scope);
        var (tenderId, _) = await StartAsync(scope);
        await service.DecideAsync(tenderId, "u.legal", Decision.Approve, Ct);
        await service.DecideAsync(tenderId, "u.tech1", Decision.Approve, Ct);

        var again = await service.DecideAsync(tenderId, "u.tech1", Decision.Approve, Ct);

        again.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        again.Error.Code.ShouldBe("workflow.already_decided");
        await using var context = await WorkflowContext(scope);
        var workflowId = await context.TenderWorkflows.Where(w => w.TenderId == tenderId).Select(w => w.Id).SingleAsync(Ct);
        var stepIds = await context.TenderSteps.Where(s => s.TenderWorkflowId == workflowId).Select(s => s.Id).ToListAsync(Ct);
        (await context.Decisions.CountAsync(d => d.UserId == "u.tech1" && stepIds.Contains(d.StepId), Ct)).ShouldBe(1);
        (await AuditRowsAsync(scope, tenderId, "workflow.refused")).ShouldHaveSingleItem()["reason"].ShouldBe("workflow.already_decided");
    }

    [Fact]
    public async Task A_decision_while_a_system_stage_is_open_is_refused()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var service = Service(scope);
        var (tenderId, _) = await StartAsync(scope);
        await service.DecideAsync(tenderId, "u.legal", Decision.Approve, Ct);
        await service.DecideAsync(tenderId, "u.tech1", Decision.Approve, Ct);
        await service.DecideAsync(tenderId, "u.tech2", Decision.Approve, Ct);

        var result = await service.DecideAsync(tenderId, "u.cfo", Decision.Approve, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Refused);
        result.Error.Code.ShouldBe("workflow.system_step");
        (await AuditRowsAsync(scope, tenderId, "workflow.refused")).ShouldHaveSingleItem()["reason"].ShouldBe("workflow.system_step");
    }

    [Fact]
    public async Task Completing_a_human_stage_as_a_system_stage_is_a_validation_error()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var (tenderId, _) = await StartAsync(scope);

        var result = await Service(scope).CompleteSystemStageAsync(tenderId, Stage.Approval, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Code.ShouldBe("workflow.not_system_stage");
        (await AuditRowsAsync(scope, tenderId, "workflow.refused")).ShouldHaveSingleItem()["reason"].ShouldBe("workflow.not_system_stage");
    }

    [Fact]
    public async Task Starting_a_tender_twice_is_a_conflict()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var (tenderId, definitionId) = await StartAsync(scope);

        var again = await Service(scope).StartAsync(tenderId, definitionId, Committee, Ct);

        again.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        again.Error.Code.ShouldBe("workflow.already_started");
    }

    [Fact]
    public async Task A_start_racing_another_start_for_the_same_tender_is_a_conflict_not_an_exception()
    {
        Guid definitionId;
        await using (var setup = _host.ScopeFor(TestTenants.Acme))
        {
            definitionId = (await Definitions(setup).SaveAsync(new SaveDefinition(null, "Race chain", false, DefaultTemplate.Steps), Ct)).Value;
        }

        // The other start: a superuser transaction inserts the tender's workflow row and holds it uncommitted, so our
        // start passes its existence check and then blocks on the unique index until the other start commits.
        var tenderId = Guid.CreateVersion7();
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var other = await owner.BeginTransactionAsync(Ct);
        await using (var insert = new NpgsqlCommand(
            "insert into workflow.tender_workflow (id, tenant_id, tender_id, definition_id, snapshot, snapshot_version, state, created_at, updated_at) "
            + "values (@id, @tenant, @tender, @definition, '{}', 1, 'Running', now(), now())",
            owner,
            other))
        {
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("tenant", TestTenants.Acme.TenantId);
            insert.Parameters.AddWithValue("tender", tenderId);
            insert.Parameters.AddWithValue("definition", definitionId);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        async Task<Result<WorkflowStatus>> StartInOwnScope()
        {
            await using var scope = _host.ScopeFor(TestTenants.Acme);
            return await Service(scope).StartAsync(tenderId, definitionId, Committee, Ct);
        }

        var start = StartInOwnScope();
        await WaitUntilBothWaitOrFinishAsync(db.OwnerConnectionString, [start]);
        await other.CommitAsync(Ct);
        var result = await start;

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Code.ShouldBe("workflow.already_started");
    }

    [Fact]
    public async Task The_chain_completes_and_financial_opening_cannot_run_before_locking()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var service = Service(scope);
        var (tenderId, _) = await StartAsync(scope);
        await service.DecideAsync(tenderId, "u.legal", Decision.Approve, Ct);
        await service.DecideAsync(tenderId, "u.tech1", Decision.Approve, Ct);
        await service.DecideAsync(tenderId, "u.tech2", Decision.Approve, Ct);

        var early = await service.CompleteSystemStageAsync(tenderId, Stage.FinancialOpening, Ct);
        early.IsSuccess.ShouldBeFalse();
        early.Error.Kind.ShouldBe(ErrorKind.InvariantViolated);
        early.Error.Message.ShouldBe("Financial opening must follow score locking (F-30).");

        (await service.CompleteSystemStageAsync(tenderId, Stage.LockScores, Ct)).IsSuccess.ShouldBeTrue();
        (await service.CompleteSystemStageAsync(tenderId, Stage.FinancialOpening, Ct)).IsSuccess.ShouldBeTrue();
        var done = (await service.DecideAsync(tenderId, "u.cfo", Decision.Approve, Ct)).Value;

        done.State.ShouldBe(WorkflowState.Completed);
        done.CurrentStage.ShouldBeNull();
    }

    [Fact]
    public async Task A_rejection_ends_the_workflow()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var service = Service(scope);
        var (tenderId, _) = await StartAsync(scope);

        var rejected = (await service.DecideAsync(tenderId, "u.legal", Decision.Reject, Ct)).Value;

        rejected.State.ShouldBe(WorkflowState.Rejected);
        (await service.DecideAsync(tenderId, "u.legal", Decision.Approve, Ct)).Error!.Kind.ShouldBe(ErrorKind.InvariantViolated);
    }

    [Fact]
    public async Task Two_last_approvers_deciding_at_once_never_leave_the_all_of_step_open()
    {
        Guid tenderId;
        await using (var setup = _host.ScopeFor(TestTenants.Acme))
        {
            (tenderId, _) = await StartAsync(setup);
            await Service(setup).DecideAsync(tenderId, "u.legal", Decision.Approve, Ct);
        }

        // Force the race: a superuser connection holds the workflow row, so both decisions load the same state and then
        // wait on it. Without the xmin guard neither touches that row, both commit, and the AllOf step stays open.
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var hold = await owner.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("select 1 from workflow.tender_workflow where tender_id = @t for update", owner, hold))
        {
            lockRow.Parameters.AddWithValue("t", tenderId);
            await lockRow.ExecuteScalarAsync(Ct);
        }

        async Task<Result<WorkflowStatus>> DecideInOwnScope(string userId)
        {
            await using var scope = _host.ScopeFor(TestTenants.Acme);
            return await Service(scope).DecideAsync(tenderId, userId, Decision.Approve, Ct);
        }

        var decisions = new[] { DecideInOwnScope("u.tech1"), DecideInOwnScope("u.tech2") };
        await WaitUntilBothWaitOrFinishAsync(db.OwnerConnectionString, decisions);
        await hold.CommitAsync(Ct);
        var results = await Task.WhenAll(decisions);

        results.Count(r => r.IsSuccess).ShouldBe(1);
        var loser = results.Single(r => !r.IsSuccess);
        loser.Error!.Kind.ShouldBe(ErrorKind.Conflict);

        // The user whose save lost retries, as the conflict message asks; the retry completes the step.
        var retryUser = results[0].IsSuccess ? "u.tech2" : "u.tech1";
        await using var check = _host.ScopeFor(TestTenants.Acme);
        var retried = (await Service(check).DecideAsync(tenderId, retryUser, Decision.Approve, Ct)).Value;

        retried.Steps[1].State.ShouldBe(StepState.Approved);
        retried.Steps[1].ApprovedBy.ShouldBe(["u.tech1", "u.tech2"], ignoreOrder: true);
        retried.CurrentStage.ShouldBe(Stage.LockScores);
    }

    private static async Task WaitUntilBothWaitOrFinishAsync(string ownerConnectionString, Task[] decisions)
    {
        // Its own connection: pg_stat_activity is a per-transaction snapshot, so the lock holder cannot poll it.
        await using var owner = new NpgsqlConnection(ownerConnectionString);
        await owner.OpenAsync(Ct);
        await using var waiting = new NpgsqlCommand(
            "select count(*) from pg_stat_activity where datname = current_database() and cardinality(pg_blocking_pids(pid)) > 0", owner);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!decisions.All(d => d.IsCompleted) && (long)(await waiting.ExecuteScalarAsync(Ct))! < decisions.Length)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the two decisions neither blocked on the workflow row nor finished");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_tenders_workflow()
    {
        Guid tenderId;
        await using (var acme = _host.ScopeFor(TestTenants.Acme))
        {
            (tenderId, _) = await StartAsync(acme);
        }

        await using var beta = _host.ScopeFor(TestTenants.Beta);
        var result = await Service(beta).GetStatusAsync(tenderId, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        // Row-level security, not only the EF query filter, hides the row.
        await using var context = await WorkflowContext(beta);
        (await context.TenderWorkflows.IgnoreQueryFilters().CountAsync(w => w.TenderId == tenderId, Ct)).ShouldBe(0);
    }

    private static async Task<IReadOnlyList<AuditRow>> AuditRowsAsync(AsyncServiceScope scope, Guid tenderId, string action)
    {
        await using var audit = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuditDbContext>>().CreateDbContextAsync(Ct);
        var tender = tenderId.ToString("D", CultureInfo.InvariantCulture);
        var rows = await audit.Events.Where(e => e.Action == action && e.SubjectId == tender).Select(e => new { e.ActorId, e.Data }).ToListAsync(Ct);
        return [.. rows.Select(r => new AuditRow(r.ActorId, Parse(r.Data)))];
    }

    private static Dictionary<string, string?> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString());
    }

    private static Task<WorkflowDbContext> WorkflowContext(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorkflowDbContext>>().CreateDbContextAsync(Ct);

    private sealed record AuditRow(string? ActorId, Dictionary<string, string?> Data)
    {
        public string? this[string key] => Data.TryGetValue(key, out var value) ? value : null;
    }

    private static async Task<(Guid TenderId, Guid DefinitionId)> StartAsync(AsyncServiceScope scope)
    {
        var definitionId = (await Definitions(scope).SaveAsync(new SaveDefinition(null, "Test chain", false, DefaultTemplate.Steps), Ct)).Value;
        var tenderId = Guid.CreateVersion7();
        var started = await Service(scope).StartAsync(tenderId, definitionId, Committee, Ct);
        started.IsSuccess.ShouldBeTrue(started.Error?.Message);
        return (tenderId, definitionId);
    }

    private static IWorkflowService Service(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IWorkflowService>();

    private static IWorkflowDefinitions Definitions(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IWorkflowDefinitions>();
}
