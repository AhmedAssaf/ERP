using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Platform.Modules.Workflow;

/// <summary>Maps save failures caused by a concurrent writer to a result instead of an exception (spec sections 4.3 and 5).</summary>
internal static class SaveErrors
{
    public static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }
}
