using Platform.Modules.Workflow.Contracts;

namespace Platform.Modules.Workflow;

/// <summary>Our own versioned snapshot format (ADR-0004 point 2). Bump CurrentVersion when the shape changes.</summary>
internal sealed record SnapshotDocument(int Version, Guid DefinitionId, int DefinitionVersion, string Name, IReadOnlyList<StepDefinition> Steps)
{
    public const int CurrentVersion = 1;
}
