using System.Reflection;
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
/// ADR-0012 on an existing database: every tenant table created under the old tenant policy (platform migrations up to
/// 0005, vendors up to 0012) is re-applied by platform migration 0006 with the staff-only rule, and the module migrations
/// that follow set the explicit exceptions (audit.events, vendor.relationships). A fresh database in the same container,
/// so the shared one is not touched.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorSessionPolicyUpgradeTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tenant_tables_of_an_existing_database_become_staff_only_and_the_exceptions_are_explicit()
    {
        var database = $"vendor_sessions_upgrade_{Guid.NewGuid():N}";
        await ExecuteAsync(db.OwnerConnectionString, $"create database {database}");
        var connectionString = new NpgsqlConnectionStringBuilder(db.OwnerConnectionString) { Database = database }.ConnectionString;
        try
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(Ct);
                await ExecuteAsync(connection, "create extension if not exists vector; create extension if not exists pgcrypto; create extension if not exists unaccent;");
                await SqlMigrator.ApplyAsync(connection, "platform", await ScriptsAsync(typeof(SharedModule).Assembly, s => string.CompareOrdinal(s, "0006") < 0), Ct);
                await AuditModule.MigrateAsync(connection, Ct);
                await TenancyModule.MigrateAsync(connection, Ct);
                await IdentityModule.MigrateAsync(connection, Ct);
                await WorkflowModule.MigrateAsync(connection, Ct);
                await OperationsModule.MigrateAsync(connection, Ct);
                await SqlMigrator.ApplyAsync(connection, "vendors", await ScriptsAsync(typeof(VendorsModule).Assembly, s => string.CompareOrdinal(s, "0013") < 0), Ct);
                // Before: identity.members under the old rule, which a vendor session passes.
                (await QualAsync(connection, "identity", "members", "tenant_isolation")).ShouldNotBeNull().ShouldNotContain("current_vendor_company");
            }

            var applied = await MigrationRunner.RunAsync(connectionString, Ct);

            applied.ShouldBe(["platform/0006_platform_vendor_sessions.sql", "vendors/0013_vendors_relationships_vendor_policy.sql", "vendors/0014_vendors_function_callers.sql"]);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(Ct);
                const string staffOnly = "platform.current_vendor_company() IS NULL";
                foreach (var table in new[] { "workflow_definition", "workflow_step", "tender_workflow", "tender_workflow_step", "step_decision" })
                {
                    (await QualAsync(connection, "workflow", table, "tenant_isolation")).ShouldNotBeNull(table).ShouldContain(staffOnly, Case.Sensitive, table);
                }

                (await QualAsync(connection, "identity", "members", "tenant_isolation")).ShouldNotBeNull().ShouldContain(staffOnly);
                (await QualAsync(connection, "audit", "events", "tenant_isolation")).ShouldNotBeNull().ShouldContain(staffOnly);
                (await QualAsync(connection, "vendor", "relationships", "tenant_vendor_isolation")).ShouldNotBeNull().ShouldContain("company_id = platform.current_vendor_company()");
                (await QualAsync(connection, "vendor", "relationships", "tenant_isolation")).ShouldBeNull();
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(db.OwnerConnectionString, $"drop database if exists {database} with (force)");
        }
    }

    private static async Task<string?> QualAsync(NpgsqlConnection connection, string schema, string table, string policy)
    {
        await using var command = new NpgsqlCommand(
            "select qual from pg_policies where schemaname = @schema and tablename = @table and policyname = @policy", connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("policy", policy);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

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
