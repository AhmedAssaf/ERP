using Platform.Shared.Results;

namespace Platform.Modules.Workflow.Contracts;

/// <summary>A tenant's workflow definitions. Changing one never affects a running tender (ADR-0003 point 2).</summary>
public interface IWorkflowDefinitions
{
    Task<Result<Guid>> SaveAsync(SaveDefinition command, CancellationToken cancellationToken = default);

    Task<Guid?> FindDefaultAsync(CancellationToken cancellationToken = default);
}
