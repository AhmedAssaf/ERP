using Platform.Modules.Workflow.Contracts;
using Platform.Shared.Results;

namespace Platform.Modules.Workflow;

/// <summary>Checks a definition on save and on start; returns every broken rule, each named (F-56 acceptance).</summary>
internal static class DefinitionValidator
{
    public static IReadOnlyList<Error> Validate(IReadOnlyList<StepDefinition> steps)
    {
        var errors = new List<Error>();
        if (steps.Count == 0)
        {
            errors.Add(Error.Validation("workflow.empty", "A workflow needs at least one step."));
            return errors;
        }

        var lockAt = IndexOf(steps, Stage.LockScores);
        var openAt = IndexOf(steps, Stage.FinancialOpening);
        if (lockAt < 0)
        {
            errors.Add(Error.Validation("workflow.lock_missing", "Score locking is a fixed point and cannot be removed (F-30)."));
        }

        if (openAt < 0)
        {
            errors.Add(Error.Validation("workflow.opening_missing", "Financial opening is a fixed point and cannot be removed (F-23)."));
        }

        if (steps.Count(s => s.Stage == Stage.LockScores) > 1 || steps.Count(s => s.Stage == Stage.FinancialOpening) > 1)
        {
            errors.Add(Error.Validation("workflow.fixed_point_repeated", "Score locking and financial opening each appear exactly once."));
        }

        if (lockAt >= 0 && openAt >= 0 && openAt < lockAt)
        {
            errors.Add(Error.Validation("workflow.opening_before_lock", "Financial opening must follow score locking (F-30)."));
        }

        for (var i = 1; i < steps.Count; i++)
        {
            var previous = steps[i - 1].Stage;
            var current = steps[i].Stage;
            var bothFixed = previous.IsSystem() && current.IsSystem();
            if (current < previous && !bothFixed)
            {
                errors.Add(Error.Validation("workflow.stage_order", $"Step {i + 1} ({current}) cannot come after {previous}; stages follow the system order."));
            }
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (string.IsNullOrWhiteSpace(step.Department))
            {
                errors.Add(Error.Validation("workflow.department_missing", $"Step {i + 1} needs a department."));
            }

            if (step.Stage.IsSystem() && step.ActorRoles.Count > 0)
            {
                errors.Add(Error.Validation("workflow.system_step_actors", $"Step {i + 1} ({step.Stage}) is run by the system and cannot have actors."));
            }

            if (!step.Stage.IsSystem() && step.ActorRoles.Count == 0)
            {
                errors.Add(Error.Validation("workflow.step_without_actors", $"Step {i + 1} ({step.Department}) needs at least one actor role."));
            }
        }

        return errors;
    }

    /// <summary>Null when valid; otherwise one validation error carrying the first code and every message.</summary>
    public static Error? Check(IReadOnlyList<StepDefinition> steps)
    {
        var errors = Validate(steps);
        return errors.Count == 0 ? null : Error.Validation(errors[0].Code, string.Join(" ", errors.Select(e => e.Message)));
    }

    private static int IndexOf(IReadOnlyList<StepDefinition> steps, Stage stage)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Stage == stage)
            {
                return i;
            }
        }

        return -1;
    }
}
