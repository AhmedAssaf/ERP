using System.Reflection;
using Npgsql;

namespace Platform.Shared.Data;

/// <summary>
/// Applies a module's embedded SQL scripts (resources named "Migrations.*.sql") in ordinal name order, each once,
/// each in its own transaction, recorded in platform.schema_migrations. An advisory lock serialises concurrent runs.
/// </summary>
public static class SqlMigrator
{
    private const long AdvisoryLockKey = 0x5741534C4249; // "WASLBI"
    private const string ResourcePrefix = "Migrations.";

    public static async Task<IReadOnlyList<string>> ApplyAsync(
        NpgsqlConnection connection, string module, Assembly assembly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(assembly);

        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        await ExecuteAsync(connection, null, $"select pg_advisory_lock({AdvisoryLockKey})", cancellationToken);
        try
        {
            await ExecuteAsync(connection, null, """
                create schema if not exists platform;
                create table if not exists platform.schema_migrations (
                    module     text        not null,
                    script     text        not null,
                    applied_at timestamptz not null default now(),
                    primary key (module, script));
                """, cancellationToken);

            var applied = new List<string>();
            foreach (var resource in resources)
            {
                var script = resource[ResourcePrefix.Length..];
                if (await IsAppliedAsync(connection, module, script, cancellationToken))
                {
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, transaction, await ReadAsync(assembly, resource, cancellationToken), cancellationToken);
                await using (var record = new NpgsqlCommand(
                    "insert into platform.schema_migrations (module, script) values (@module, @script)", connection, transaction))
                {
                    record.Parameters.AddWithValue("module", module);
                    record.Parameters.AddWithValue("script", script);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                applied.Add(script);
            }

            return applied;
        }
        finally
        {
            await ExecuteAsync(connection, null, $"select pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    private static async Task<bool> IsAppliedAsync(NpgsqlConnection connection, string module, string script, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists (select 1 from platform.schema_migrations where module = @module and script = @script)", connection);
        command.Parameters.AddWithValue("module", module);
        command.Parameters.AddWithValue("script", script);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<string> ReadAsync(Assembly assembly, string resource, CancellationToken cancellationToken)
    {
        await using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded migration '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

#pragma warning disable CA2100 // The SQL comes from scripts compiled into our own assemblies, never from user input.
    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
#pragma warning restore CA2100
}
