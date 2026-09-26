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
                   exists (select 1 from pg_policy p where p.polrelid = c.oid and p.polname = 'tenant_isolation')
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p')
              and n.nspname not in ('platform', 'pg_catalog', 'information_schema')
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
}
