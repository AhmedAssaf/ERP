using System.Reflection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;
using Platform.Modules.Vendors;

namespace Platform.IntegrationTests.Security;

/// <summary>
/// Security-definer functions that read or write across forced row-level security (tenancy.update_branding reads
/// identity.members and vendor.vendor_users, ops.write_platform_audit inserts into ops.platform_audit) only work when
/// their owner bypasses row-level security. A migration run by a role without BYPASSRLS would create them owned by that
/// role, so they would silently see nothing. Every module that creates such functions refuses to run as such a role, as
/// the vendors migrations always have (review of the vendor slice, 2026-09-28).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MigrationOwnerGuardTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> GuardedScripts => new()
    {
        { "vendors", "0014_vendors_function_callers.sql" },
        { "tenancy", "0008_tenancy_branding_vendor_users_and_owner.sql" },
        { "operations", "0005_operations_owner_guard.sql" },
        { "identity", "0002_identity_staff_tenants.sql" },
        { "vendors", "0027_vendors_superseded_outcome_kept.sql" },
    };

    [Theory]
    [MemberData(nameof(GuardedScripts))]
    public async Task A_migration_creating_definer_functions_refuses_a_role_without_bypassrls(string module, string script)
    {
        var sql = await ReadScriptAsync(module, script);
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var probe = new NpgsqlCommand("create role migration_guard_probe nologin nobypassrls; set local role migration_guard_probe;", connection, transaction))
        {
            await probe.ExecuteNonQueryAsync(Ct);
        }

#pragma warning disable CA2100 // The script is one of the modules' own embedded migrations.
        await using var run = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
        var refused = await Should.ThrowAsync<PostgresException>(() => run.ExecuteNonQueryAsync(Ct));

        refused.MessageText.ShouldContain("BYPASSRLS");
        await transaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Every_security_definer_function_is_owned_by_a_role_that_bypasses_row_level_security()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select n.nspname || '.' || p.proname
            from pg_proc p
            join pg_namespace n on n.oid = p.pronamespace
            join pg_roles r on r.oid = p.proowner
            where p.prosecdef
              and n.nspname in ('platform', 'audit', 'tenancy', 'identity', 'workflow', 'ops', 'vendor')
              and not (r.rolsuper or r.rolbypassrls)
            order by 1
            """, connection);
        var offenders = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            offenders.Add(reader.GetString(0));
        }

        offenders.ShouldBeEmpty();
    }

    private static async Task<string> ReadScriptAsync(string module, string script)
    {
        var assembly = module switch
        {
            "vendors" => typeof(VendorsModule).Assembly,
            "tenancy" => typeof(TenancyModule).Assembly,
            "operations" => typeof(OperationsModule).Assembly,
            "identity" => typeof(IdentityModule).Assembly,
            _ => throw new ArgumentOutOfRangeException(nameof(module), module, null),
        };

        await using var stream = assembly.GetManifestResourceStream("Migrations." + script)
            ?? throw new InvalidOperationException($"The {module} module has no migration {script}.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Ct);
    }
}
