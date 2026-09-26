namespace Platform.Modules.Workflow.Contracts;

/// <summary>One step of a tenant's definition. Threshold is stored now and evaluated by F-09 later.</summary>
public sealed record StepDefinition(
    Stage Stage,
    string Department,
    StepRule Rule,
    IReadOnlyList<string> ActorRoles,
    decimal? Threshold = null);

/// <summary>Create (DefinitionId null) or replace a definition. Replacing increments its version.</summary>
public sealed record SaveDefinition(Guid? DefinitionId, string Name, bool IsDefault, IReadOnlyList<StepDefinition> Steps);
