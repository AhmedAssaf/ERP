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

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await WaitForAppRoleAsync();
        await MigrationRunner.RunAsync(OwnerConnectionString);
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
