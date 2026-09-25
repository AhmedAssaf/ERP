using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace ElsaWorkflowSpike;

public record ApprovalBookmark(string Step);

// The custom step timed for question 4: a department approval with any-of or all-of.
// It suspends on a bookmark and resumes when a named approver decides.
[Activity("WaslaBid", "Tender", "A department approves or rejects; any-of or all-of the named approvers.")]
public class ApprovalStep : Activity
{
    [Input(Description = "Step name shown to approvers.")]
    public Input<string> StepName { get; set; } = new("");

    [Input(Description = "Department that acts.")]
    public Input<string> Department { get; set; } = new("");

    [Input(Description = "AnyOf or AllOf.")]
    public Input<string> Rule { get; set; } = new("AnyOf");

    [Input(Description = "Comma-separated user ids allowed to decide.")]
    public Input<string> Approvers { get; set; } = new("");

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        context.CreateBookmark(new ApprovalBookmark(context.Get(StepName)!), OnDecisionAsync);
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnDecisionAsync(ActivityExecutionContext context)
    {
        var step = context.Get(StepName)!;
        var actor = context.GetWorkflowInput<string>("Actor");
        var decision = context.GetWorkflowInput<string>("Decision");
        var approvers = context.Get(Approvers)!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var tender = TenderStore.Get(context.WorkflowExecutionContext.CorrelationId!);

        // An unauthorised decision must not fault the tender: refuse, audit, and keep waiting.
        // Throwing here would put the whole instance in Faulted (see the first spike run).
        if (!approvers.Contains(actor))
        {
            tender.Audit.Add($"{step}: refused decision from {actor} (not an approver)");
            context.CreateBookmark(new ApprovalBookmark(step), OnDecisionAsync);
            return;
        }

        tender.RecordDecision(step, actor, decision);

        if (decision == "Rejected")
        {
            await context.CompleteActivityWithOutcomesAsync("Rejected");
            return;
        }

        var approved = context.GetProperty<string>("approved") is { Length: > 0 } s ? s.Split(',').ToHashSet() : [];
        approved.Add(actor);
        context.SetProperty("approved", string.Join(',', approved));

        if (context.Get(Rule) == "AllOf" && !approvers.All(approved.Contains))
        {
            // Wait for the remaining approvers on a fresh bookmark.
            context.CreateBookmark(new ApprovalBookmark(step), OnDecisionAsync);
            return;
        }

        await context.CompleteActivityWithOutcomesAsync("Approved");
    }
}

// Fixed points: thin wrappers that call the domain. The guard lives in Tender, so a
// definition that reorders them fails at runtime even if it slipped past validation.
[Activity("WaslaBid", "Fixed points", "Locks technical scores (F-30). Cannot be skipped.")]
public class LockScores : CodeActivity
{
    protected override void Execute(ActivityExecutionContext context) =>
        TenderStore.Get(context.WorkflowExecutionContext.CorrelationId!).LockScores("system");
}

[Activity("WaslaBid", "Fixed points", "Opens financial envelopes. Requires locked scores.")]
public class OpenFinancial : CodeActivity
{
    protected override void Execute(ActivityExecutionContext context) =>
        TenderStore.Get(context.WorkflowExecutionContext.CorrelationId!).OpenFinancial("system");
}
