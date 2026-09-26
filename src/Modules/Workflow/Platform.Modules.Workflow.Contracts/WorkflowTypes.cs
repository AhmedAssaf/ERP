namespace Platform.Modules.Workflow.Contracts;

/// <summary>Stages in system order (ADR-0003 point 3). A definition decides who acts; it cannot reorder stages.</summary>
public enum Stage
{
    Screening = 1,
    TechnicalScoring = 2,
    LockScores = 3,
    FinancialOpening = 4,
    Approval = 5,
    Award = 6,
}

public enum StepRule
{
    AnyOf,
    AllOf,
}

public enum Decision
{
    Approve,
    Reject,
}

public enum WorkflowState
{
    Running,
    Completed,
    Rejected,
}

public enum StepState
{
    Pending,
    Open,
    Approved,
    Rejected,
    Done,
}

public static class StageExtensions
{
    /// <summary>Fixed points completed by the owning domain module, never by a person's decision.</summary>
    public static bool IsSystem(this Stage stage) => stage is Stage.LockScores or Stage.FinancialOpening;
}
