using Npgsql;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Data;

/// <summary>Guards every later migration: a table with a tenant_id column must have forced RLS and the isolation policy.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenantTableCatalogTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_tenant_owned_table_has_forced_row_level_security_and_the_isolation_policy()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select n.nspname || '.' || c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   exists (select 1 from pg_policy p where p.polrelid = c.oid and p.polname in ('tenant_isolation', 'tenant_vendor_isolation'))
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p')
              and n.nspname not in ('pg_catalog', 'information_schema')
              -- The tenant registry itself is read before any tenant is known (host resolution), so it cannot be tenant-filtered.
              and (n.nspname, c.relname) not in (('tenancy', 'tenants'), ('tenancy', 'tenant_hosts'))
              and exists (select 1 from pg_attribute a
                          where a.attrelid = c.oid and a.attname = 'tenant_id' and a.attnum > 0 and not a.attisdropped)
            order by 1
            """, connection);

        var tables = new List<(string Name, bool Enabled, bool Forced, bool HasPolicy)>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3)));
            }
        }

        tables.ShouldNotBeEmpty();
        tables.Where(t => !(t.Enabled && t.Forced && t.HasPolicy)).Select(t => t.Name).ShouldBeEmpty();
    }

    /// <summary>
    /// ADR-0012: every tenant table uses one of the two policies, compared with what the helpers produce on a scratch table:
    /// the staff-only policy of <c>platform.enable_tenant_rls</c> (a session with a vendor context sees nothing), or the
    /// policy of <c>platform.enable_tenant_vendor_rls</c> on its company column (a vendor sees only its own rows), chosen
    /// explicitly for the tables a vendor reads. <c>audit.events</c> is the one table with two policies: vendors write
    /// their actions into the tenant's log, and only staff read it.
    /// </summary>
    [Fact]
    public async Task Every_tenant_table_uses_the_staff_only_policy_or_the_explicit_vendor_policy()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        var (staff, vendor) = await ReferencePoliciesAsync(connection);
        staff.ShouldContain("current_vendor_company() IS NULL");
        vendor.ShouldContain("company_id = platform.current_vendor_company()");

        await using var command = new NpgsqlCommand("""
            select p.schemaname || '.' || p.tablename, p.policyname, p.cmd, coalesce(p.qual, ''), coalesce(p.with_check, '')
            from pg_policies p
            join pg_namespace n on n.nspname = p.schemaname
            join pg_class c on c.relnamespace = n.oid and c.relname = p.tablename
            where p.schemaname not in ('pg_catalog', 'information_schema')
              and exists (select 1 from pg_attribute a
                          where a.attrelid = c.oid and a.attname = 'tenant_id' and a.attnum > 0 and not a.attisdropped)
            order by 1, 2
            """, connection);
        var policies = new List<(string Table, string Name, string Command, string Using, string Check)>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                policies.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }
        }

        var byTable = policies.GroupBy(p => p.Table).ToDictionary(g => g.Key, g => g.ToList());
        byTable.Keys.ShouldContain("vendor.relationships");
        byTable.Keys.ShouldContain("identity.members");
        var wrong = new List<string>();
        foreach (var (table, rows) in byTable)
        {
            var ok = table switch
            {
                "vendor.relationships" => rows is [{ Name: "tenant_vendor_isolation", Command: "ALL" } only] && only.Using == vendor && only.Check == vendor,
                "audit.events" => rows.Count == 2
                    && rows.Any(r => r is { Name: "tenant_isolation", Command: "SELECT" } && r.Using == staff)
                    && rows.Any(r => r is { Name: "tenant_audit_insert", Command: "INSERT" }
                        && r.Check == "((tenant_id = platform.current_tenant()) AND ((platform.current_vendor_company() IS NULL) OR (actor_id = platform.current_user_id())))"),
                _ => rows is [{ Name: "tenant_isolation", Command: "ALL" } one] && one.Using == staff && one.Check == staff,
            };
            if (!ok)
            {
                wrong.Add($"{table}: {string.Join("; ", rows.Select(r => $"{r.Name} {r.Command} using {r.Using} check {r.Check}"))}");
            }
        }

        wrong.ShouldBeEmpty();
    }

    /// <summary>
    /// A view runs with its owner's rights and, unless it is a security-invoker view, ignores the row-level security of the
    /// tables under it, so no view may read a tenant table (ADR-0012). Covers every schema, the platform schema included.
    /// </summary>
    [Fact]
    public async Task No_view_reads_a_tenant_table()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select distinct vn.nspname || '.' || v.relname || ' reads ' || tn.nspname || '.' || t.relname
            from pg_class v
            join pg_namespace vn on vn.oid = v.relnamespace
            join pg_rewrite r on r.ev_class = v.oid
            join pg_depend d on d.classid = 'pg_rewrite'::regclass and d.objid = r.oid and d.refclassid = 'pg_class'::regclass
            join pg_class t on t.oid = d.refobjid and t.oid <> v.oid
            join pg_namespace tn on tn.oid = t.relnamespace
            where v.relkind in ('v', 'm')
              and vn.nspname not in ('pg_catalog', 'information_schema')
              and exists (select 1 from pg_attribute a
                          where a.attrelid = t.oid and a.attname = 'tenant_id' and a.attnum > 0 and not a.attisdropped)
            order by 1
            """, connection);
        var views = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            views.Add(reader.GetString(0));
        }

        views.ShouldBeEmpty();
    }

    /// <summary>The expressions the two helpers write, read back from a scratch table in a rolled-back transaction.</summary>
    private static async Task<(string Staff, string Vendor)> ReferencePoliciesAsync(NpgsqlConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand("""
            create table platform.rls_reference_staff (tenant_id uuid);
            create table platform.rls_reference_vendor (tenant_id uuid, company_id uuid);
            select platform.enable_tenant_rls('platform', 'rls_reference_staff');
            select platform.enable_tenant_vendor_rls('platform', 'rls_reference_vendor', 'company_id');
            select (select qual from pg_policies where schemaname = 'platform' and tablename = 'rls_reference_staff'),
                   (select qual from pg_policies where schemaname = 'platform' and tablename = 'rls_reference_vendor');
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (reader.FieldCount != 2 || !reader.HasRows)
        {
            (await reader.NextResultAsync(Ct)).ShouldBeTrue();
        }

        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        var result = (reader.GetString(0), reader.GetString(1));
        await reader.DisposeAsync();
        await transaction.RollbackAsync(Ct);
        return result;
    }

    /// <summary>
    /// Vendor tables are either tenant-scoped (the relationship) or platform-level and keyed on the vendor company
    /// (spec section 2). Two exceptions are platform reference data, readable by everyone and without row-level security:
    /// the recipient list and the CR ownership check method (W-33). The disputes (W-33) belong to no company or tenant
    /// yet, so they have their own forced policy: only a session without a tenant or vendor context (the platform console)
    /// reads them.
    /// </summary>
    [Fact]
    public async Task Every_vendor_table_has_forced_row_level_security_with_its_policy_and_the_exceptions_are_explicit()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   exists (select 1 from pg_policy p
                           where p.polrelid = c.oid and p.polname in ('tenant_isolation', 'tenant_vendor_isolation', 'vendor_isolation'))
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p') and n.nspname = 'vendor' and c.relname not in ('recipients', 'ownership_settings', 'cr_disputes')
            order by 1
            """, connection);

        var tables = new List<(string Name, bool Enabled, bool Forced, bool HasPolicy)>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3)));
            }
        }

        tables.Select(t => t.Name).ShouldBe(
            ["companies", "consent_events", "documents", "ownership_verifications", "relationships", "uploads", "vendor_users"]);
        tables.Where(t => !(t.Enabled && t.Forced && t.HasPolicy)).Select(t => t.Name).ShouldBeEmpty();

        await using var disputes = new NpgsqlCommand("""
            select c.relrowsecurity, c.relforcerowsecurity,
                   (select string_agg(p.polname || ' ' || p.polcmd::text, ', ') from pg_policy p where p.polrelid = c.oid),
                   has_table_privilege('erp_app', c.oid, 'INSERT') or has_table_privilege('erp_app', c.oid, 'UPDATE')
                       or has_table_privilege('erp_app', c.oid, 'DELETE')
            from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'vendor' and c.relname = 'cr_disputes'
            """, connection);
        await using (var reader = await disputes.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            reader.GetBoolean(0).ShouldBeTrue();
            reader.GetBoolean(1).ShouldBeTrue();
            reader.GetString(2).ShouldBe("cr_dispute_access r");
            reader.GetBoolean(3).ShouldBeFalse("disputes change only through their security-definer functions");
        }

        await using var settings = new NpgsqlCommand("""
            select has_table_privilege('erp_app', 'vendor.ownership_settings', 'SELECT'),
                   has_table_privilege('erp_app', 'vendor.ownership_settings', 'INSERT')
                       or has_table_privilege('erp_app', 'vendor.ownership_settings', 'UPDATE')
                       or has_table_privilege('erp_app', 'vendor.ownership_settings', 'DELETE'),
                   has_table_privilege('erp_app', 'vendor.ownership_verifications', 'SELECT')
            """, connection);
        await using var granted = await settings.ExecuteReaderAsync(Ct);
        (await granted.ReadAsync(Ct)).ShouldBeTrue();
        granted.GetBoolean(0).ShouldBeTrue("every session reads the method");
        granted.GetBoolean(1).ShouldBeFalse("the method changes only through vendor.set_ownership_method");
        granted.GetBoolean(2).ShouldBeFalse("staff read a verification only through vendor.related_ownership");
    }
}
