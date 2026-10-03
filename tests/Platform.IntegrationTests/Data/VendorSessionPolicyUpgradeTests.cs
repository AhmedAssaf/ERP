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
/// ADR-0012 on an existing database, in the real order: from the state before that work (platform up to 0005, audit
/// 0001, tenancy up to 0006, identity 0001, operations up to 0003, vendors up to 0012), MigrationRunner re-applies every tenant table with
/// the staff-only rule (platform 0006) and the module migrations that follow set the explicit exceptions (audit.events
/// with exactly a SELECT and an INSERT policy, vendor.relationships with the vendor helper). A fresh database in the same container,
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
                // The state before ADR-0012 and the second pentest, in MigrationRunner's order: each module at its last
                // migration before that work.
                await SqlMigrator.ApplyAsync(connection, "platform", await ScriptsAsync(typeof(SharedModule).Assembly, s => Before(s, "0006")), Ct);
                await SqlMigrator.ApplyAsync(connection, "audit", await ScriptsAsync(typeof(AuditModule).Assembly, s => Before(s, "0002")), Ct);
                await SqlMigrator.ApplyAsync(connection, "tenancy", await ScriptsAsync(typeof(TenancyModule).Assembly, s => Before(s, "0007")), Ct);
                await SqlMigrator.ApplyAsync(connection, "identity", await ScriptsAsync(typeof(IdentityModule).Assembly, s => Before(s, "0002")), Ct);
                await WorkflowModule.MigrateAsync(connection, Ct);
                await SqlMigrator.ApplyAsync(connection, "operations", await ScriptsAsync(typeof(OperationsModule).Assembly, s => Before(s, "0004")), Ct);
                await SqlMigrator.ApplyAsync(connection, "vendors", await ScriptsAsync(typeof(VendorsModule).Assembly, s => Before(s, "0013")), Ct);
                // Before: identity.members under the old rule, which a vendor session passes.
                (await QualAsync(connection, "identity", "members", "tenant_isolation")).ShouldNotBeNull().ShouldNotContain("current_vendor_company");
            }

            var applied = await MigrationRunner.RunAsync(connectionString, Ct);

            applied.ShouldBe(
            [
                "platform/0006_platform_vendor_sessions.sql",
                "platform/0007_platform_data_protection_keys.sql",
                "platform/0008_platform_worker_role.sql",
                "jobs/0001_jobs_recurring_entries_and_replay.sql",
                "audit/0002_audit_vendor_insert.sql",
                "audit/0003_audit_actor_and_time.sql",
                "tenancy/0007_tenancy_function_callers.sql",
                "tenancy/0008_tenancy_branding_vendor_users_and_owner.sql",
                "tenancy/0009_tenancy_referenced_logos.sql",
                "tenancy/0010_tenancy_worker_role.sql",
                "identity/0002_identity_staff_tenants.sql",
                "identity/0003_identity_user_activity.sql",
                "identity/0004_identity_worker_role.sql",
                "operations/0004_operations_platform_audit_access.sql",
                "operations/0005_operations_owner_guard.sql",
                "operations/0006_operations_active_user_counts.sql",
                "operations/0007_operations_worker_role.sql",
                "operations/0008_operations_platform_audit_actor.sql",
                "vendors/0013_vendors_relationships_vendor_policy.sql",
                "vendors/0014_vendors_function_callers.sql",
                "vendors/0015_vendors_consent_actor_and_start.sql",
                "vendors/0016_vendors_approver_and_consent_rules.sql",
                "vendors/0017_vendors_consent_rule_names.sql",
                "vendors/0018_vendors_cr_ownership.sql",
                "vendors/0019_vendors_cr_ownership_hardening.sql",
                "vendors/0020_vendors_dispute_cap_flag.sql",
                "vendors/0021_vendors_dispute_listing_order.sql",
                "vendors/0022_vendors_claimant_awaiting_identity_provider.sql",
                "vendors/0023_vendors_claimant_awaiting_organization.sql",
                "vendors/0024_vendors_dispute_retry_scope.sql",
                "vendors/0025_vendors_superseded_dispute_removals.sql",
                "vendors/0026_vendors_latest_dispute_and_removal_standing.sql",
                "vendors/0027_vendors_superseded_outcome_kept.sql",
                "vendors/0028_vendors_worker_role.sql",
            ]);
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
                (await PoliciesAsync(connection, "audit", "events")).ShouldBe(["tenant_audit_insert INSERT", "tenant_isolation SELECT"]);
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

    private static bool Before(string script, string number) => string.CompareOrdinal(script, number) < 0;

    private static async Task<IReadOnlyList<string>> PoliciesAsync(NpgsqlConnection connection, string schema, string table)
    {
        await using var command = new NpgsqlCommand(
            "select policyname || ' ' || cmd from pg_policies where schemaname = @schema and tablename = @table order by 1", connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        var policies = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            policies.Add(reader.GetString(0));
        }

        return policies;
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
