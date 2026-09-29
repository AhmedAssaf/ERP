using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;
using Platform.Shared.Data;

namespace Platform.IntegrationTests.Data;

[Collection(DatabaseCollection.Name)]
public class MigrationTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_are_journaled_and_a_second_run_applies_nothing()
    {
        var secondRun = await MigrationRunner.RunAsync(db.OwnerConnectionString, Ct);

        secondRun.ShouldBeEmpty();
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select count(*) from platform.schema_migrations where module = 'platform'", connection);
        var count = (long)(await command.ExecuteScalarAsync(Ct))!;
        count.ShouldBe(7);
    }

    [Fact]
    public async Task Every_applied_script_records_its_checksum()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select count(*) from platform.schema_migrations where checksum is null", connection);

        var missing = (long)(await command.ExecuteScalarAsync(Ct))!;

        missing.ShouldBe(0);
    }

    [Fact]
    public async Task A_script_changed_after_it_was_applied_is_refused()
    {
        var module = $"checksum-{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);

        var first = await SqlMigrator.ApplyAsync(connection, module, [("0001_a.sql", "select 1")], Ct);
        first.ShouldBe(["0001_a.sql"]);
        var unchanged = await SqlMigrator.ApplyAsync(connection, module, [("0001_a.sql", "select 1")], Ct);
        unchanged.ShouldBeEmpty();

        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => SqlMigrator.ApplyAsync(connection, module, [("0001_a.sql", "select 2")], Ct));

        refused.Message.ShouldContain(module);
        refused.Message.ShouldContain("0001_a.sql");
    }

    [Fact]
    public async Task A_line_ending_difference_is_not_a_changed_script()
    {
        var module = $"checksum-{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);

        await SqlMigrator.ApplyAsync(connection, module, [("0001_a.sql", "select 1;\r\nselect 2;")], Ct);
        var again = await SqlMigrator.ApplyAsync(connection, module, [("0001_a.sql", "select 1;\nselect 2;")], Ct);

        again.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_journal_row_recorded_before_checksums_is_accepted_and_backfilled()
    {
        var module = $"checksum-{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using (var legacy = new NpgsqlCommand(
            "insert into platform.schema_migrations (module, script, checksum) values (@module, '0001_a.sql', null)", connection))
        {
            legacy.Parameters.AddWithValue("module", module);
            await legacy.ExecuteNonQueryAsync(Ct);
        }

        var applied = await SqlMigrator.ApplyAsync(connection, module, [("0001_a.sql", "select 1")], Ct);

        applied.ShouldBeEmpty();
        await Should.ThrowAsync<InvalidOperationException>(
            () => SqlMigrator.ApplyAsync(connection, module, [("0001_a.sql", "select 2")], Ct));
    }

    [Fact]
    public async Task The_app_role_cannot_execute_the_rls_helper()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select platform.enable_tenant_rls('audit', 'events')", connection);

        var refused = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Current_tenant_is_null_when_nothing_is_set()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select platform.current_tenant()", connection);

        var result = await command.ExecuteScalarAsync(Ct);

        result.ShouldBe(DBNull.Value);
    }
}
