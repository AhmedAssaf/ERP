namespace Platform.Modules.Workflow.Contracts;

public sealed record StepStatus(
    int Position,
    Stage Stage,
    string Department,
    StepRule Rule,
    StepState State,
    IReadOnlyList<string> AssignedUsers,
    IReadOnlyList<string> ApprovedBy);

public sealed record WorkflowStatus(
    Guid TenderId,
    WorkflowState State,
    int? CurrentPosition,
    Stage? CurrentStage,
    IReadOnlyList<string> PendingUsers,
    IReadOnlyList<StepStatus> Steps);
