namespace Platform.Modules.Workflow.Contracts;

/// <summary>The pilot's default chain (docs/05 row 16): contracts, technical evaluators, locking, opening, finance.</summary>
public static class DefaultTemplate
{
    public const string Name = "Default approval chain";

    public static IReadOnlyList<StepDefinition> Steps { get; } =
    [
        new(Stage.Screening, "Contracts", StepRule.AnyOf, ["contracts"]),
        new(Stage.TechnicalScoring, "Technical committee", StepRule.AllOf, ["evaluator"]),
        new(Stage.LockScores, "System", StepRule.AnyOf, []),
        new(Stage.FinancialOpening, "System", StepRule.AnyOf, []),
        new(Stage.Approval, "Finance", StepRule.AnyOf, ["finance"]),
    ];
}
