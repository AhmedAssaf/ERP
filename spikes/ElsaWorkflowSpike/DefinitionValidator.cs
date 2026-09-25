using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;

namespace ElsaWorkflowSpike;

// Publish-time check on an Elsa workflow, whether built in code (Sequence) or drawn in
// Studio (Flowchart). Rule: LockScores must exist, and every path from the start to
// OpenFinancial must pass through LockScores.
public static class DefinitionValidator
{
    public static List<string> Validate(Workflow workflow)
    {
        var errors = new List<string>();
        switch (workflow.Root)
        {
            case Sequence seq:
                ValidateSequence(seq.Activities.ToList(), errors);
                break;
            case Flowchart flow:
                ValidateFlowchart(flow, errors);
                break;
            default:
                errors.Add($"Unsupported root {workflow.Root.GetType().Name}; only Sequence and Flowchart are allowed.");
                break;
        }
        return errors;
    }

    private static void ValidateSequence(List<IActivity> steps, List<string> errors)
    {
        var lockAt = steps.FindIndex(a => a is LockScores);
        var openAt = steps.FindIndex(a => a is OpenFinancial);
        if (lockAt < 0) errors.Add("LockScores is missing.");
        if (openAt < 0) errors.Add("OpenFinancial is missing.");
        if (lockAt >= 0 && openAt >= 0 && openAt < lockAt) errors.Add("OpenFinancial comes before LockScores.");
        if (steps.Any(a => a is not (ApprovalStep or LockScores or OpenFinancial)))
            errors.Add("Only ApprovalStep, LockScores and OpenFinancial are allowed at the top level.");
    }

    private static void ValidateFlowchart(Flowchart flow, List<string> errors)
    {
        var nodes = flow.Activities.ToList();
        if (!nodes.Any(a => a is LockScores)) errors.Add("LockScores is missing.");
        if (!nodes.Any(a => a is OpenFinancial)) errors.Add("OpenFinancial is missing.");
        if (nodes.Any(a => a is not (ApprovalStep or LockScores or OpenFinancial or Start or End)))
            errors.Add("Only ApprovalStep, LockScores, OpenFinancial, Start and End are allowed.");

        var edges = flow.Connections.GroupBy(c => c.Source.Activity.Id)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Target.Activity).ToList());
        var start = flow.Start ?? nodes.FirstOrDefault(a => a is Start) ?? nodes.FirstOrDefault(a => flow.Connections.All(c => c.Target.Activity.Id != a.Id));
        if (start is null) { errors.Add("Flowchart has no start."); return; }

        // Walk from the start without passing through LockScores; reaching OpenFinancial is a violation.
        var seen = new HashSet<string>();
        var stack = new Stack<IActivity>([start]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node.Id) || node is LockScores) continue;
            if (node is OpenFinancial) { errors.Add("A path reaches OpenFinancial without passing LockScores."); return; }
            foreach (var next in edges.GetValueOrDefault(node.Id, [])) stack.Push(next);
        }
    }
}
