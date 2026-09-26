using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;

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
        count.ShouldBe(1);
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
