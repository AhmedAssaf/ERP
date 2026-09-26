using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Modules.Workflow;

namespace Platform.UnitTests.Workflow;

public class SaveErrorsTests
{
    [Fact]
    public void A_unique_violation_anywhere_in_the_inner_chain_is_recognised()
    {
        var unique = new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);

        SaveErrors.IsUniqueViolation(new DbUpdateException("save failed", unique)).ShouldBeTrue();
        SaveErrors.IsUniqueViolation(new DbUpdateException("save failed", new InvalidOperationException("wrapped", unique))).ShouldBeTrue();
    }

    [Fact]
    public void Other_database_errors_are_not_mistaken_for_a_unique_violation()
    {
        var foreignKey = new PostgresException("fk", "ERROR", "ERROR", PostgresErrorCodes.ForeignKeyViolation);

        SaveErrors.IsUniqueViolation(new DbUpdateException("save failed", foreignKey)).ShouldBeFalse();
        SaveErrors.IsUniqueViolation(new DbUpdateException("save failed")).ShouldBeFalse();
    }
}
