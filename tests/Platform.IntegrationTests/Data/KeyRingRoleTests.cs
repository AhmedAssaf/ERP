using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;
using Platform.Shared.Data;

namespace Platform.IntegrationTests.Data;

/// <summary>
/// W-24 (N-10): the migrator gives <c>erp_key_ring</c> its login by sending only a SCRAM-SHA-256 verifier it computed,
/// never the password. The verifier matches what PostgreSQL itself stores for the same password and salt, and the role
/// logs in with the password afterwards.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class KeyRingRoleTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Our_verifier_is_the_one_postgresql_computes_for_the_same_password_and_salt()
    {
        var role = $"scram_probe_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        try
        {
            // The server hashes a plaintext password itself here (a throwaway role, a random password).
#pragma warning disable CA2100 // Generated role name and hex password.
            await using (var create = new NpgsqlCommand($"set password_encryption = 'scram-sha-256'; create role {role} login password '{password}'", owner))
#pragma warning restore CA2100
            {
                await create.ExecuteNonQueryAsync(Ct);
            }

            var stored = await StoredVerifierAsync(owner, role);
            var head = stored.Split('$')[1].Split(':');

            ScramSha256.Verifier(password, Convert.FromBase64String(head[1]), int.Parse(head[0], System.Globalization.CultureInfo.InvariantCulture))
                .ShouldBe(stored);
        }
        finally
        {
#pragma warning disable CA2100 // Generated role name.
            await using var drop = new NpgsqlCommand($"drop role if exists {role}", owner);
#pragma warning restore CA2100
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_key_ring_role_logs_in_with_the_password_after_the_migrator_sent_only_its_verifier()
    {
        await KeyRingRole.EnableLoginAsync(db.OwnerConnectionString, db.KeyRingConnectionString, Ct);

        await using (var owner = new NpgsqlConnection(db.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            var stored = await StoredVerifierAsync(owner, KeyRingRole.Name);
            stored.ShouldStartWith("SCRAM-SHA-256$4096:");
            stored.ShouldNotContain(TestSecrets.KeyRingPassword);
        }

        await using var ring = new NpgsqlConnection(db.KeyRingConnectionString);
        await ring.OpenAsync(Ct);
        await using var who = new NpgsqlCommand("select current_user", ring);
        ((string)(await who.ExecuteScalarAsync(Ct))!).ShouldBe(KeyRingRole.Name);
    }

    [Fact]
    public async Task The_migrator_refuses_a_key_ring_connection_string_for_another_role()
    {
        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            KeyRingRole.EnableLoginAsync(db.OwnerConnectionString, db.AppConnectionString, Ct));

        refused.Message.ShouldContain(KeyRingRole.Name);
    }

    private static async Task<string> StoredVerifierAsync(NpgsqlConnection owner, string role)
    {
        await using var read = new NpgsqlCommand("select rolpassword from pg_authid where rolname = @role", owner);
        read.Parameters.AddWithValue("role", role);
        return (string)(await read.ExecuteScalarAsync(Ct))!;
    }
}
