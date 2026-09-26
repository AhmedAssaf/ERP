using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Platform.Shared.Data;

/// <summary>
/// Applies a module's embedded SQL scripts (resources named "Migrations.*.sql") in ordinal name order, each once,
/// each in its own transaction, recorded in platform.schema_migrations. An advisory lock serialises concurrent runs.
/// Each script runs in its own transaction, so <c>create index concurrently</c> is not supported.
/// The SHA-256 of each script (line endings normalised to LF) is journaled; a script changed after it was applied is
/// refused. Journal rows written before checksums existed are accepted and backfilled with the current checksum.
/// </summary>
public static class SqlMigrator
{
    private const long AdvisoryLockKey = 0x5741534C4249; // "WASLBI"
    private const string ResourcePrefix = "Migrations.";

    public static async Task<IReadOnlyList<string>> ApplyAsync(
        NpgsqlConnection connection, string module, Assembly assembly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        var scripts = new List<(string Script, string Sql)>(resources.Count);
        foreach (var resource in resources)
        {
            scripts.Add((resource[ResourcePrefix.Length..], await ReadAsync(assembly, resource, cancellationToken)));
        }

        return await ApplyAsync(connection, module, scripts, cancellationToken);
    }

    /// <summary>Applies the given scripts in list order; the public overload feeds it the ordered embedded resources.</summary>
    internal static async Task<IReadOnlyList<string>> ApplyAsync(
        NpgsqlConnection connection, string module, IReadOnlyList<(string Script, string Sql)> scripts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(scripts);

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
                alter table platform.schema_migrations add column if not exists checksum text;
                """, cancellationToken);

            var applied = new List<string>();
            foreach (var (script, sql) in scripts)
            {
                var checksum = Checksum(sql);
                var journal = await ReadJournalAsync(connection, module, script, cancellationToken);
                if (journal.Applied)
                {
                    if (journal.Checksum is null)
                    {
                        await RecordChecksumAsync(connection, module, script, checksum, cancellationToken);
                    }
                    else if (!string.Equals(journal.Checksum, checksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Migration {module}/{script} was changed after it was applied. Add a new script instead of editing an applied one.");
                    }

                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, transaction, sql, cancellationToken);
                await using (var record = new NpgsqlCommand(
                    "insert into platform.schema_migrations (module, script, checksum) values (@module, @script, @checksum)", connection, transaction))
                {
                    record.Parameters.AddWithValue("module", module);
                    record.Parameters.AddWithValue("script", script);
                    record.Parameters.AddWithValue("checksum", checksum);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                applied.Add(script);
            }

            return applied;
        }
        finally
        {
            // On a broken connection the unlock would throw and hide the original exception; the lock dies with the session.
            if (connection.State == ConnectionState.Open)
            {
                await ExecuteAsync(connection, null, $"select pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
            }
        }
    }

    // Line endings are normalised so a checkout with CRLF (Windows, core.autocrlf) and one with LF hash the same.
    private static string Checksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));

    private static async Task<(bool Applied, string? Checksum)> ReadJournalAsync(
        NpgsqlConnection connection, string module, string script, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select checksum from platform.schema_migrations where module = @module and script = @script", connection);
        command.Parameters.AddWithValue("module", module);
        command.Parameters.AddWithValue("script", script);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (false, null);
        }

        return (true, await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetString(0));
    }

    private static async Task RecordChecksumAsync(
        NpgsqlConnection connection, string module, string script, string checksum, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "update platform.schema_migrations set checksum = @checksum where module = @module and script = @script and checksum is null",
            connection);
        command.Parameters.AddWithValue("module", module);
        command.Parameters.AddWithValue("script", script);
        command.Parameters.AddWithValue("checksum", checksum);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
