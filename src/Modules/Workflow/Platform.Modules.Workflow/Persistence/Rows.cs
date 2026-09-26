using Platform.Modules.Workflow.Contracts;

namespace Platform.Modules.Workflow.Persistence;

internal sealed class DefinitionRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Version { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<DefinitionStepRow> Steps { get; set; } = [];
}

internal sealed class DefinitionStepRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DefinitionId { get; set; }

    public int Position { get; set; }

    public Stage Stage { get; set; }

    public string Department { get; set; } = string.Empty;

    public StepRule Rule { get; set; }

    public List<string> ActorRoles { get; set; } = [];

    public decimal? Threshold { get; set; }
}

internal sealed class TenderWorkflowRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TenderId { get; set; }

    public Guid DefinitionId { get; set; }

    public string Snapshot { get; set; } = "{}";

    public int SnapshotVersion { get; set; }

    public WorkflowState State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>PostgreSQL xmin, the optimistic concurrency token.</summary>
    public uint Version { get; set; }

    public List<TenderStepRow> Steps { get; set; } = [];
}

internal sealed class TenderStepRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TenderWorkflowId { get; set; }

    public int Position { get; set; }

    public Stage Stage { get; set; }

    public string Department { get; set; } = string.Empty;

    public StepRule Rule { get; set; }

    public List<string> AssignedUsers { get; set; } = [];

    public StepState Status { get; set; }

    public List<StepDecisionRow> Decisions { get; set; } = [];
}

internal sealed class StepDecisionRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid StepId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public Decision Decision { get; set; }

    public DateTimeOffset DecidedAt { get; set; }
}
