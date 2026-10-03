using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;

namespace Platform.IntegrationTests.Jobs;

/// <summary>
/// W-42 fix round 2 (review m-2): on a database whose job sequence has never handed out an id, the application role cannot
/// take id 1 ahead of the first job (the sequence reports last_value 1 with is_called false); the client's own insert, which
/// draws its id from the sequence, still works. A fresh database in the same container, so the shared one is not touched.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class JobIdSequenceTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_application_role_cannot_take_the_first_id_of_an_unused_job_sequence()
    {
        var database = $"job_sequence_{Guid.NewGuid():N}";
        await ExecuteAsync(db.OwnerConnectionString, $"create database {database}");
        var owner = new NpgsqlConnectionStringBuilder(db.OwnerConnectionString) { Database = database }.ConnectionString;
        var app = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Database = database }.ConnectionString;
        try
        {
            await ExecuteAsync(owner, "create extension if not exists vector; create extension if not exists pgcrypto; create extension if not exists unaccent;");
            await MigrationRunner.RunAsync(owner, Ct);

            await using var connection = new NpgsqlConnection(app);
            await connection.OpenAsync(Ct);
            var refused = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(
                connection, "insert into hangfire.job (id, invocationdata, arguments, createdat) values (1, '{}'::jsonb, '[]'::jsonb, now())"));
            refused.MessageText.ShouldContain("row-level security");

            await ExecuteAsync(connection, "insert into hangfire.job (invocationdata, arguments, createdat) values ('{}'::jsonb, '[]'::jsonb, now())");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(db.OwnerConnectionString, $"drop database if exists {database} with (force)");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // The tests' own statements, with a generated database name.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }
}
