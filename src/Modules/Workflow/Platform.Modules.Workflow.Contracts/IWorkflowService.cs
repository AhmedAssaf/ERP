using Platform.Shared.Results;

namespace Platform.Modules.Workflow.Contracts;

/// <summary>The executor (ADR-0004): runs a tender's snapshot of a definition. It never reads tender data.</summary>
public interface IWorkflowService
{
    /// <summary>Snapshots the definition onto the tender, assigns users per role, and opens the first step.</summary>
    Task<Result<WorkflowStatus>> StartAsync(
        Guid tenderId,
        Guid definitionId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> assignmentsByRole,
        CancellationToken cancellationToken = default);

    /// <summary>A decision by an assigned user on the open human step. Others are refused and audited.</summary>
    Task<Result<WorkflowStatus>> DecideAsync(Guid tenderId, string userId, Decision decision, CancellationToken cancellationToken = default);

    /// <summary>Called by the owning domain module when scores are locked or envelopes opened.</summary>
    Task<Result<WorkflowStatus>> CompleteSystemStageAsync(Guid tenderId, Stage stage, CancellationToken cancellationToken = default);

    Task<Result<WorkflowStatus>> GetStatusAsync(Guid tenderId, CancellationToken cancellationToken = default);
}
