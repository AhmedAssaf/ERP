using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Workflow.Contracts;
using Platform.Modules.Workflow.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Workflow;

/// <summary>
/// Our own state machine (ADR-0004): walks a tender's snapshot between the fixed points. A refused action never faults
/// the tender; it is audited and the step keeps waiting (docs/06 section 6.3).
/// </summary>
internal sealed class WorkflowService(
    IDbContextFactory<WorkflowDbContext> contexts,
    ITenantAccessor tenants,
    IAuditWriter audit,
    TimeProvider clock) : IWorkflowService
{
    public async Task<Result<WorkflowStatus>> StartAsync(
        Guid tenderId,
        Guid definitionId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> assignmentsByRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignmentsByRole);
        var tenantId = RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var definition = await db.Definitions.AsNoTracking().Include(d => d.Steps)
            .SingleOrDefaultAsync(d => d.Id == definitionId, cancellationToken);
        if (definition is null)
        {
            return Result.Failure<WorkflowStatus>(Error.NotFound("workflow.definition_not_found", "The workflow definition was not found."));
        }

        var steps = definition.Steps.OrderBy(s => s.Position)
            .Select(s => new StepDefinition(s.Stage, s.Department, s.Rule, s.ActorRoles, s.Threshold))
            .ToList();
        if (DefinitionValidator.Check(steps) is { } invalid)
        {
            return Result.Failure<WorkflowStatus>(invalid);
        }

        if (await db.TenderWorkflows.AnyAsync(w => w.TenderId == tenderId, cancellationToken))
        {
            return Result.Failure<WorkflowStatus>(AlreadyStarted());
        }

        var now = clock.GetUtcNow();
        var workflow = new TenderWorkflowRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            TenderId = tenderId,
            DefinitionId = definition.Id,
            SnapshotVersion = SnapshotDocument.CurrentVersion,
            Snapshot = SnapshotDocument.Serialize(definition.Id, definition.Version, definition.Name, steps),
            State = WorkflowState.Running,
            CreatedAt = now,
            UpdatedAt = now,
        };

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            List<string> users = step.Stage.IsSystem()
                ? []
                : [.. step.ActorRoles
                    .SelectMany(role => assignmentsByRole.TryGetValue(role, out var assigned) ? assigned : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)];
            if (!step.Stage.IsSystem() && users.Count == 0)
            {
                return Result.Failure<WorkflowStatus>(Error.Validation(
                    "workflow.step_unassigned",
                    $"Step {i + 1} ({step.Department}) has nobody assigned for the roles {string.Join(", ", step.ActorRoles)}."));
            }

            workflow.Steps.Add(new TenderStepRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                TenderWorkflowId = workflow.Id,
                Position = i + 1,
                Stage = step.Stage,
                Department = step.Department,
                Rule = step.Rule,
                AssignedUsers = users,
                Status = i == 0 ? StepState.Open : StepState.Pending,
            });
        }

        db.TenderWorkflows.Add(workflow);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SaveErrors.IsUniqueViolation(exception))
        {
            // Another start for this tender committed between the existence check and this save.
            return Result.Failure<WorkflowStatus>(AlreadyStarted());
        }

        // The change and its audit row are two transactions; the outbox that makes them atomic is F-41.
        await audit.WriteAsync(
            new AuditEntry(null, "workflow.started", "tender", Format(tenderId), new Dictionary<string, string?>
            {
                ["definition"] = Format(definition.Id),
                ["definitionVersion"] = Format(definition.Version),
            }),
            // the change is committed; do not let a cancelled request skip its audit row
            CancellationToken.None);
        return Result.Success(ToStatus(workflow));
    }

    public async Task<Result<WorkflowStatus>> DecideAsync(Guid tenderId, string userId, Decision decision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var tenantId = RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var workflow = await LoadAsync(db, tenderId, cancellationToken);
        if (workflow is null)
        {
            return NotFound();
        }

        if (workflow.State != WorkflowState.Running)
        {
            return await RefuseAsync(tenderId, userId, NotRunning(workflow), null, null, cancellationToken);
        }

        var step = OpenStep(workflow);
        if (step.Stage.IsSystem())
        {
            return await RefuseAsync(
                tenderId,
                userId,
                Error.Refused("workflow.system_step", $"The current step ({Name(step.Stage)}) is completed by the system, not by a decision."),
                step.Stage,
                step.Position,
                cancellationToken);
        }

        if (!step.AssignedUsers.Contains(userId, StringComparer.Ordinal))
        {
            return await RefuseAsync(
                tenderId,
                userId,
                Error.Refused("workflow.not_assigned", $"You are not assigned to step {step.Position} ({step.Department})."),
                step.Stage,
                step.Position,
                cancellationToken);
        }

        if (step.Decisions.Any(d => d.UserId == userId))
        {
            return await RefuseAsync(
                tenderId, userId, Error.Conflict("workflow.already_decided", "You have already decided on this step."), step.Stage, step.Position, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var row = new StepDecisionRow { Id = Guid.CreateVersion7(), TenantId = tenantId, StepId = step.Id, UserId = userId, Decision = decision, DecidedAt = now };
        // Add marks the row Added and EF fixup appends it to the tracked step's Decisions; adding it there as well duplicates it.
        db.Decisions.Add(row);

        if (decision == Decision.Reject)
        {
            step.Status = StepState.Rejected;
            workflow.State = WorkflowState.Rejected;
        }
        else if (step.Rule == StepRule.AnyOf || step.AssignedUsers.All(u => step.Decisions.Any(d => d.UserId == u && d.Decision == Decision.Approve)))
        {
            step.Status = StepState.Approved;
            OpenNextStep(workflow);
        }

        // Touch the workflow row on every decision so its xmin guards against two decisions racing (spec section 7).
        workflow.UpdatedAt = now;
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return Result.Failure<WorkflowStatus>(conflict);
        }

        var status = ToStatus(workflow);
        await audit.WriteAsync(
            new AuditEntry(userId, "workflow.decided", "tender", Format(tenderId), new Dictionary<string, string?>
            {
                ["step"] = Format(step.Position),
                ["decision"] = Name(decision),
                ["stepState"] = Name(step.Status),
                ["pending"] = string.Join(",", status.PendingUsers),
            }),
            // the change is committed; do not let a cancelled request skip its audit row
            CancellationToken.None);
        return Result.Success(status);
    }

    public async Task<Result<WorkflowStatus>> CompleteSystemStageAsync(Guid tenderId, Stage stage, CancellationToken cancellationToken = default)
    {
        RequireTenant();
        if (!stage.IsSystem())
        {
            return await RefuseAsync(
                tenderId,
                null,
                Error.Validation("workflow.not_system_stage", $"{Name(stage)} is decided by people, not completed by the system."),
                stage,
                null,
                cancellationToken);
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var workflow = await LoadAsync(db, tenderId, cancellationToken);
        if (workflow is null)
        {
            return NotFound();
        }

        if (workflow.State != WorkflowState.Running)
        {
            return await RefuseAsync(tenderId, null, NotRunning(workflow), stage, null, cancellationToken);
        }

        var step = OpenStep(workflow);
        if (step.Stage != stage)
        {
            var message = stage == Stage.FinancialOpening && step.Stage <= Stage.LockScores
                ? "Financial opening must follow score locking (F-30)."
                : $"{Name(stage)} is not the current step; the workflow is at {Name(step.Stage)}.";
            return await RefuseAsync(tenderId, null, Error.Invariant("workflow.stage_not_current", message), stage, step.Position, cancellationToken);
        }

        step.Status = StepState.Done;
        OpenNextStep(workflow);
        workflow.UpdatedAt = clock.GetUtcNow();
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return Result.Failure<WorkflowStatus>(conflict);
        }

        await audit.WriteAsync(
            new AuditEntry(null, "workflow.system_stage_completed", "tender", Format(tenderId), new Dictionary<string, string?>
            {
                ["stage"] = Name(stage),
            }),
            // the change is committed; do not let a cancelled request skip its audit row
            CancellationToken.None);
        return Result.Success(ToStatus(workflow));
    }

    public async Task<Result<WorkflowStatus>> GetStatusAsync(Guid tenderId, CancellationToken cancellationToken = default)
    {
        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var workflow = await LoadAsync(db, tenderId, cancellationToken);
        return workflow is null ? NotFound() : Result.Success(ToStatus(workflow));
    }

    private Guid RequireTenant() =>
        tenants.Current?.TenantId ?? throw new InvalidOperationException("Workflow operations need a current tenant.");

    private static Task<TenderWorkflowRow?> LoadAsync(WorkflowDbContext db, Guid tenderId, CancellationToken cancellationToken) =>
        db.TenderWorkflows
            .Include(w => w.Steps).ThenInclude(s => s.Decisions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(w => w.TenderId == tenderId, cancellationToken);

    private static async Task<Error?> TrySaveAsync(WorkflowDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentUpdate();
        }
        catch (DbUpdateException exception) when (SaveErrors.IsUniqueViolation(exception))
        {
            // e.g. the same user's decision on the same step saved twice at once (step_id, user_id is unique)
            return ConcurrentUpdate();
        }
    }

    private static Error ConcurrentUpdate() =>
        Error.Conflict("workflow.concurrent_update", "Another action was saved at the same moment. Reload and try again.");

    private static Error AlreadyStarted() =>
        Error.Conflict("workflow.already_started", "A workflow is already running for this tender.");

    /// <summary>Audits a refused action (spec 4.4) before anything is saved, then returns the refusal.</summary>
    private async Task<Result<WorkflowStatus>> RefuseAsync(
        Guid tenderId, string? actorId, Error error, Stage? stage, int? step, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string?> { ["reason"] = error.Code };
        if (step is { } position)
        {
            data["step"] = Format(position);
        }

        if (stage is { } named)
        {
            data["stage"] = Name(named);
        }

        await audit.WriteAsync(new AuditEntry(actorId, "workflow.refused", "tender", Format(tenderId), data), cancellationToken);
        return Result.Failure<WorkflowStatus>(error);
    }

    private static TenderStepRow OpenStep(TenderWorkflowRow workflow) =>
        workflow.Steps.OrderBy(s => s.Position).First(s => s.Status == StepState.Open);

    private static void OpenNextStep(TenderWorkflowRow workflow)
    {
        var next = workflow.Steps.OrderBy(s => s.Position).FirstOrDefault(s => s.Status == StepState.Pending);
        if (next is null)
        {
            workflow.State = WorkflowState.Completed;
        }
        else
        {
            next.Status = StepState.Open;
        }
    }

    private static WorkflowStatus ToStatus(TenderWorkflowRow workflow)
    {
        var steps = workflow.Steps.OrderBy(s => s.Position)
            .Select(s => new StepStatus(
                s.Position,
                s.Stage,
                s.Department,
                s.Rule,
                s.Status,
                s.AssignedUsers,
                s.Decisions.Where(d => d.Decision == Decision.Approve).OrderBy(d => d.DecidedAt).Select(d => d.UserId).ToList()))
            .ToList();
        var open = steps.FirstOrDefault(s => s.State == StepState.Open);
        IReadOnlyList<string> pending = open is null || open.Stage.IsSystem()
            ? []
            : open.AssignedUsers.Except(open.ApprovedBy, StringComparer.Ordinal).ToList();
        return new WorkflowStatus(workflow.TenderId, workflow.State, open?.Position, open?.Stage, pending, steps);
    }

    private static Result<WorkflowStatus> NotFound() =>
        Result.Failure<WorkflowStatus>(Error.NotFound("workflow.not_found", "No workflow exists for this tender."));

    private static Error NotRunning(TenderWorkflowRow workflow) =>
        Error.Invariant("workflow.not_running", $"The workflow is {Name(workflow.State)}; no further actions are accepted.");

    private static string Format(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum => Enum.GetName(value) ?? string.Empty;
}
