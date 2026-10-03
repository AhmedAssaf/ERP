using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;

namespace Platform.IntegrationTests.Data;

/// <summary>
/// W-36 (N-10): the migrator gives <c>erp_worker</c> its login as it does for the key ring (W-24), by sending only a
/// SCRAM-SHA-256 verifier, never the password, and only for a connection string that names that role.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WorkerRoleLoginTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_worker_role_logs_in_with_the_password_after_the_migrator_sent_only_its_verifier()
    {
        await WorkerRole.EnableLoginAsync(db.OwnerConnectionString, db.WorkerConnectionString, Ct);

        await using (var owner = new NpgsqlConnection(db.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            await using var read = new NpgsqlCommand("select rolpassword from pg_authid where rolname = @role", owner);
            read.Parameters.AddWithValue("role", WorkerRole.Name);
            var stored = (string)(await read.ExecuteScalarAsync(Ct))!;
            stored.ShouldStartWith("SCRAM-SHA-256$4096:");
            stored.ShouldNotContain(TestSecrets.WorkerPassword);
        }

        await using var worker = new NpgsqlConnection(db.WorkerConnectionString);
        await worker.OpenAsync(Ct);
        await using var who = new NpgsqlCommand("select current_user, pg_has_role(current_user, 'erp_app', 'usage'), pg_has_role(current_user, 'erp_app', 'set')", worker);
        await using var reader = await who.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        reader.GetString(0).ShouldBe(WorkerRole.Name);
        reader.GetBoolean(1).ShouldBeTrue("the worker inherits the application role's rights");
        reader.GetBoolean(2).ShouldBeFalse("the worker cannot switch to the application role");
    }

    [Fact]
    public async Task The_migrator_refuses_a_worker_connection_string_for_another_role()
    {
        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            WorkerRole.EnableLoginAsync(db.OwnerConnectionString, db.AppConnectionString, Ct));

        refused.Message.ShouldContain(WorkerRole.Name);
    }
}
