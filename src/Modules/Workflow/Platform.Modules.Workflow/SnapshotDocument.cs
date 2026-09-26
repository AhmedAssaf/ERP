using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Modules.Workflow.Contracts;

namespace Platform.Modules.Workflow;

/// <summary>
/// Our own versioned snapshot format (ADR-0004 point 2). It owns its shape: renaming a contract property must not change
/// the stored JSON. Bump CurrentVersion when the shape changes.
/// </summary>
internal static class SnapshotDocument
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string Serialize(Guid definitionId, int definitionVersion, string name, IReadOnlyList<StepDefinition> steps) =>
        JsonSerializer.Serialize(
            new Document(
                CurrentVersion,
                definitionId,
                definitionVersion,
                name,
                [.. steps.Select(s => new SnapshotStep(s.Stage, s.Department, s.Rule, [.. s.ActorRoles], s.Threshold))]),
            Json);

    // v1 JSON: version, definitionId, definitionVersion, name, steps[stage, department, rule, actorRoles, threshold].
    private sealed record Document(int Version, Guid DefinitionId, int DefinitionVersion, string Name, IReadOnlyList<SnapshotStep> Steps);

    private sealed record SnapshotStep(Stage Stage, string Department, StepRule Rule, IReadOnlyList<string> ActorRoles, decimal? Threshold);
}
