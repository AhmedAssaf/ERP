using System.Reflection;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;
using Platform.Modules.Vendors;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Shared.Data;

namespace Platform.IntegrationTests.Data;

/// <summary>
/// W-36 on an existing database (every developer database, the pilot later): before it, a host prepared Hangfire's tables
/// at its first start as <c>erp_app</c>, which therefore owned them. MigrationRunner then applies the five W-36 scripts,
/// moves the tables to the migration owner, leaves the application role what enqueueing needs, and the worker-only
/// functions leave the application role. A fresh database in the same container, so the shared one is not touched.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WorkerRoleUpgradeTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hangfire_tables_created_by_the_app_role_move_to_the_owner_and_the_app_role_still_enqueues()
    {
        var database = $"worker_role_upgrade_{Guid.NewGuid():N}";
        await ExecuteAsync(db.OwnerConnectionString, $"create database {database}");
        var owner = new NpgsqlConnectionStringBuilder(db.OwnerConnectionString) { Database = database }.ConnectionString;
        var app = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Database = database }.ConnectionString;
        try
        {
            await using (var connection = new NpgsqlConnection(owner))
            {
                await connection.OpenAsync(Ct);
                await ExecuteAsync(connection, "create extension if not exists vector; create extension if not exists pgcrypto; create extension if not exists unaccent;");
                // The state before W-36, in MigrationRunner's order: each module at its last migration before this work.
                await SqlMigrator.ApplyAsync(connection, "platform", await ScriptsAsync(typeof(SharedModule).Assembly, s => Before(s, "0008")), Ct);
                await AuditModule.MigrateAsync(connection, Ct);
                await SqlMigrator.ApplyAsync(connection, "tenancy", await ScriptsAsync(typeof(TenancyModule).Assembly, s => Before(s, "0010")), Ct);
                await SqlMigrator.ApplyAsync(connection, "identity", await ScriptsAsync(typeof(IdentityModule).Assembly, s => Before(s, "0004")), Ct);
                await WorkflowModule.MigrateAsync(connection, Ct);
                await SqlMigrator.ApplyAsync(connection, "operations", await ScriptsAsync(typeof(OperationsModule).Assembly, s => Before(s, "0007")), Ct);
                await SqlMigrator.ApplyAsync(connection, "vendors", await ScriptsAsync(typeof(VendorsModule).Assembly, s => Before(s, "0028")), Ct);
            }

            // What a host did at its first start before W-36 (PrepareSchemaIfNecessary as erp_app).
            await using (var connection = new NpgsqlConnection(app))
            {
                await connection.OpenAsync(Ct);
                PostgreSqlObjectsInstaller.Install(connection, "hangfire");
            }

            (await OwnersAsync(owner)).ShouldBe(["erp_app"]);

            var applied = await MigrationRunner.RunAsync(owner, Ct);

            applied.ShouldBe(
            [
                "platform/0008_platform_worker_role.sql",
                "tenancy/0010_tenancy_worker_role.sql",
                "identity/0004_identity_worker_role.sql",
                "operations/0007_operations_worker_role.sql",
                "vendors/0028_vendors_worker_role.sql",
            ]);
            (await OwnersAsync(owner)).ShouldBe([new NpgsqlConnectionStringBuilder(owner).Username!]);

            // The web host's client, as erp_app, enqueues on the taken-over tables; the job is stored.
            var storage = new PostgreSqlStorage(
                new NpgsqlConnectionFactory(app, new PostgreSqlStorageOptions()),
                new PostgreSqlStorageOptions { SchemaName = "hangfire", PrepareSchemaIfNecessary = false });
            var jobId = new BackgroundJobClient(storage).Enqueue(() => UpgradeProbe.Run());
            using (var storageConnection = storage.GetConnection())
            {
                storageConnection.GetJobData(jobId).ShouldNotBeNull();
            }

            await using (var connection = new NpgsqlConnection(app))
            {
                await connection.OpenAsync(Ct);
                await using var probe = new NpgsqlCommand(
                    "select has_function_privilege('erp_app', 'vendor.stale_uploads()', 'execute'), has_function_privilege('erp_worker', 'vendor.stale_uploads()', 'execute'), has_schema_privilege('erp_app', 'hangfire', 'create')",
                    connection);
                await using var reader = await probe.ExecuteReaderAsync(Ct);
                (await reader.ReadAsync(Ct)).ShouldBeTrue();
                reader.GetBoolean(0).ShouldBeFalse("the application role lost the worker's function");
                reader.GetBoolean(1).ShouldBeTrue("the worker's role has it");
                reader.GetBoolean(2).ShouldBeFalse("the application role creates nothing in hangfire any more");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(db.OwnerConnectionString, $"drop database if exists {database} with (force)");
        }
    }

    private static async Task<List<string>> OwnersAsync(string ownerConnectionString)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select distinct pg_get_userbyid(c.relowner) from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'hangfire' and c.relkind in ('r', 'S')",
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var owners = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            owners.Add(reader.GetString(0));
        }

        return owners;
    }

    private static bool Before(string script, string number) => string.CompareOrdinal(script, number) < 0;

    private static async Task<List<(string Script, string Sql)>> ScriptsAsync(Assembly assembly, Func<string, bool> include)
    {
        const string prefix = "Migrations.";
        var scripts = new List<(string Script, string Sql)>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var script = resource[prefix.Length..];
            if (!include(script))
            {
                continue;
            }

            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            scripts.Add((script, await reader.ReadToEndAsync(Ct)));
        }

        return scripts;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await ExecuteAsync(connection, sql);
    }

#pragma warning disable CA2100 // Test SQL built from generated ids and constants only.
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
#pragma warning restore CA2100
}

/// <summary>A job that only needs to be stored, never run.</summary>
public static class UpgradeProbe
{
    public static void Run()
    {
    }
}
