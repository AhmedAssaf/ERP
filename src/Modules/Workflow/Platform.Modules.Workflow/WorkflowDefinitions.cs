using Microsoft.EntityFrameworkCore;
using Platform.Modules.Workflow.Contracts;
using Platform.Modules.Workflow.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Workflow;

internal sealed class WorkflowDefinitions(IDbContextFactory<WorkflowDbContext> contexts, ITenantAccessor tenants, TimeProvider clock) : IWorkflowDefinitions
{
    public async Task<Result<Guid>> SaveAsync(SaveDefinition command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return Result.Failure<Guid>(Error.Validation("workflow.name_missing", "A workflow definition needs a name."));
        }

        if (DefinitionValidator.Check(command.Steps) is { } invalid)
        {
            return Result.Failure<Guid>(invalid);
        }

        var tenantId = tenants.Current?.TenantId ?? throw new InvalidOperationException("Workflow operations need a current tenant.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        DefinitionRow row;
        if (command.DefinitionId is { } id)
        {
            var existing = await db.Definitions.Include(d => d.Steps).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
            if (existing is null)
            {
                return Result.Failure<Guid>(Error.NotFound("workflow.definition_not_found", "The workflow definition was not found."));
            }

            db.DefinitionSteps.RemoveRange(existing.Steps);
            existing.Steps.Clear();
            existing.Name = command.Name;
            existing.IsDefault = command.IsDefault;
            existing.Version += 1;
            row = existing;
        }
        else
        {
            row = new DefinitionRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = command.Name,
                Version = 1,
                IsDefault = command.IsDefault,
                CreatedAt = clock.GetUtcNow(),
            };
            db.Definitions.Add(row);
        }

        if (command.IsDefault)
        {
            await db.Definitions.Where(d => d.IsDefault && d.Id != row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsDefault, false), cancellationToken);
        }

        row.Steps.AddRange(command.Steps.Select((step, index) => new DefinitionStepRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            DefinitionId = row.Id,
            Position = index + 1,
            Stage = step.Stage,
            Department = step.Department,
            Rule = step.Rule,
            ActorRoles = [.. step.ActorRoles],
            Threshold = step.Threshold,
        }));

        // On a conflict the transaction is rolled back when it is disposed.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<Guid>(Concurrent());
        }
        catch (DbUpdateException exception) when (SaveErrors.IsUniqueViolation(exception))
        {
            return Result.Failure<Guid>(Concurrent());
        }

        await transaction.CommitAsync(cancellationToken);
        return Result.Success(row.Id);
    }

    public async Task<Guid?> FindDefaultAsync(CancellationToken cancellationToken = default)
    {
        _ = tenants.Current ?? throw new InvalidOperationException("Workflow operations need a current tenant.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Definitions.Where(d => d.IsDefault).Select(d => (Guid?)d.Id).SingleOrDefaultAsync(cancellationToken);
    }

    private static Error Concurrent() =>
        Error.Conflict("workflow.concurrent_update", "Another change to the workflow definitions was saved at the same moment. Reload and try again.");
}
