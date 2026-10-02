using Npgsql;
using Platform.Migrator;
using Testcontainers.PostgreSql;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>One PostgreSQL container per test run, initialised exactly like infra/compose, with all migrations applied.</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    // Development password of the app role, created by infra/compose/postgres/init/01-databases.sql.
    private const string AppRolePassword = "erp_app_dev_password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("platform")
        .WithUsername("erp")
        .WithPassword("erp_test_owner")
        .WithResourceMapping(new FileInfo(RepoPaths.PostgresInitScript), "/docker-entrypoint-initdb.d/")
        .Build();

    public string OwnerConnectionString => _container.GetConnectionString();

    public string AppConnectionString =>
        new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Username = "erp_app", Password = AppRolePassword }.ConnectionString;

    /// <summary>The key ring's own role (W-24), <c>erp_key_ring</c>, the only one with rights on the Data Protection keys.</summary>
    public string KeyRingConnectionString => TestSecrets.KeyRingConnectionString(OwnerConnectionString);

    /// <summary>
    /// The worker's own role (W-36), <c>erp_worker</c>: what <c>erp_app</c> may do plus the worker-only functions and ops
    /// writes. Every test that stands for the worker connects with this, never with <see cref="AppConnectionString"/>.
    /// </summary>
    public string WorkerConnectionString => TestSecrets.WorkerConnectionString(OwnerConnectionString);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await WaitForAppRoleAsync();
        await MigrationRunner.RunAsync(OwnerConnectionString);
        // As the migrator does when ConnectionStrings:KeyRing is set. The role is cluster-wide, so every database the tests
        // create in this container shares the login.
        await KeyRingRole.EnableLoginAsync(OwnerConnectionString, KeyRingConnectionString);
        // W-36: as the migrator does when ConnectionStrings:Worker is set; cluster-wide as well.
        await WorkerRole.EnableLoginAsync(OwnerConnectionString, WorkerConnectionString);
        await DevSeed.SeedTenantsAsync(OwnerConnectionString);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    // The entrypoint runs init scripts on a temporary server before the final start; wait until the app role can log in.
    private async Task WaitForAppRoleAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(AppConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
