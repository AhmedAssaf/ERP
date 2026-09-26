using Platform.Modules.Workflow;
using Platform.Modules.Workflow.Contracts;

namespace Platform.UnitTests.Workflow;

public class DefinitionValidatorTests
{
    private static StepDefinition Human(Stage stage, string role, StepRule rule = StepRule.AnyOf) => new(stage, "Department", rule, [role]);

    private static StepDefinition System(Stage stage) => new(stage, "System", StepRule.AnyOf, []);

    [Fact]
    public void The_default_template_is_valid() =>
        DefinitionValidator.Validate(DefaultTemplate.Steps).ShouldBeEmpty();

    [Fact]
    public void Financial_opening_before_score_locking_is_rejected_with_the_rule_named()
    {
        var errors = DefinitionValidator.Validate(
            [Human(Stage.Screening, "contracts"), System(Stage.FinancialOpening), System(Stage.LockScores), Human(Stage.Approval, "finance")]);

        errors.Select(e => e.Code).ShouldBe(["workflow.opening_before_lock"]);
        errors[0].Message.ShouldBe("Financial opening must follow score locking (F-30).");
    }

    [Fact]
    public void Removing_a_fixed_point_is_rejected()
    {
        var errors = DefinitionValidator.Validate([Human(Stage.Screening, "contracts"), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldContain("workflow.lock_missing");
    }

    [Fact]
    public void A_human_stage_out_of_system_order_is_rejected()
    {
        var errors = DefinitionValidator.Validate(
            [Human(Stage.Approval, "finance"), Human(Stage.Screening, "contracts"), System(Stage.LockScores), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldContain("workflow.stage_order");
    }

    [Fact]
    public void A_human_step_without_actors_is_rejected()
    {
        var errors = DefinitionValidator.Validate(
            [new StepDefinition(Stage.Screening, "Contracts", StepRule.AnyOf, []), System(Stage.LockScores), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldBe(["workflow.step_without_actors"]);
    }

    [Fact]
    public void A_system_step_with_actors_is_rejected()
    {
        var errors = DefinitionValidator.Validate(
            [Human(Stage.Screening, "contracts"), new StepDefinition(Stage.LockScores, "System", StepRule.AnyOf, ["someone"]), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldBe(["workflow.system_step_actors"]);
    }

    [Fact]
    public void An_undefined_stage_is_rejected()
    {
        var errors = DefinitionValidator.Validate(
            [Human((Stage)99, "contracts"), System(Stage.LockScores), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldContain("workflow.unknown_stage");
    }

    [Fact]
    public void An_undefined_rule_is_rejected()
    {
        var errors = DefinitionValidator.Validate(
            [Human(Stage.Screening, "contracts", (StepRule)7), System(Stage.LockScores), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldBe(["workflow.unknown_rule"]);
    }

    [Fact]
    public void Missing_actor_roles_are_a_validation_error_not_an_exception()
    {
        var errors = DefinitionValidator.Validate(
            [new StepDefinition(Stage.Screening, "Contracts", StepRule.AnyOf, null!), System(Stage.LockScores), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldBe(["workflow.actor_roles_missing"]);
    }

    [Fact]
    public void Check_combines_every_broken_rule_into_one_error()
    {
        var error = DefinitionValidator.Check([]);

        error.ShouldNotBeNull();
        error.Code.ShouldBe("workflow.empty");
    }
}
