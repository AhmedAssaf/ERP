using Npgsql;
using Platform.Shared;

namespace Platform.Migrator;

/// <summary>Applies every module's SQL migrations as the owner role, in dependency order.</summary>
public static class MigrationRunner
{
    public static async Task<IReadOnlyList<string>> RunAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        var applied = new List<string>();
        applied.AddRange(Named("platform", await SharedModule.MigrateAsync(connection, cancellationToken)));
        return applied;
    }

    private static IEnumerable<string> Named(string module, IEnumerable<string> scripts) => scripts.Select(s => $"{module}/{s}");
}
