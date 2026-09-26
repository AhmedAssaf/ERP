using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;

namespace Platform.IntegrationTests.Tenancy;

[Collection(DatabaseCollection.Name)]
public class TenantDirectoryTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> MalformedHosts => ["", " ", "a b", new string('a', 254), "acme.localhost/x", "acme.localhost:8443", "ünï.localhost"];

    [Fact]
    public async Task Known_host_resolves_to_the_tenant_ignoring_case()
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(null);

        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindByHostAsync("ACME.localhost", Ct);

        tenant.ShouldBe(TestTenants.Acme);
    }

    [Fact]
    public async Task Unknown_host_resolves_to_nothing()
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(null);

        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindByHostAsync("nobody.localhost", Ct);

        tenant.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(MalformedHosts))]
    public async Task Malformed_host_resolves_to_nothing_without_touching_the_database(string malformed)
    {
        // The data source points at a closed port: any query would fail, so a null result proves no query ran.
        var unreachable = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Host = "127.0.0.1", Port = 1, Timeout = 1 }.ConnectionString;
        await using var host = new ModuleHost(unreachable);
        await using var scope = host.ScopeFor(null);

        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindByHostAsync(malformed, Ct);

        tenant.ShouldBeNull();
    }

    [Fact]
    public async Task Invalidate_drops_a_cached_miss()
    {
        const string newHost = "invalidate-test.localhost";
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(null);
        var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
        (await directory.FindByHostAsync(newHost, Ct)).ShouldBeNull();

        await ExecuteAsOwnerAsync($"insert into tenancy.tenant_hosts (host, tenant_id) values ('{newHost}', '{TestTenants.Acme.TenantId}')");
        try
        {
            (await directory.FindByHostAsync(newHost, Ct)).ShouldBeNull("the miss is still cached");

            directory.Invalidate("INVALIDATE-TEST.localhost");

            (await directory.FindByHostAsync(newHost, Ct)).ShouldBe(TestTenants.Acme);
        }
        finally
        {
            await ExecuteAsOwnerAsync($"delete from tenancy.tenant_hosts where host = '{newHost}'");
        }
    }

    [Fact]
    public async Task The_app_role_cannot_read_the_tenant_tables_directly()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select count(*) from tenancy.tenants", connection);

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task The_app_role_cannot_read_the_host_table_directly()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select count(*) from tenancy.tenant_hosts", connection);

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task The_app_role_cannot_create_tables_in_the_tenancy_schema()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("create table tenancy.x (id int)", connection);

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_logo_url_that_is_not_https_is_refused()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            insert into tenancy.tenants (id, slug, keycloak_org_alias, portal_name, primary_color, logo_url)
            values (gen_random_uuid(), 'logo-test', 'logo-test', 'Logo Test', '#000000', 'javascript:alert(1)')
            """, connection);

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.ShouldBe("ck_tenants_logo_url");
    }

    [Fact]
    public async Task An_https_logo_url_is_accepted()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand("""
            insert into tenancy.tenants (id, slug, keycloak_org_alias, portal_name, primary_color, logo_url)
            values (gen_random_uuid(), 'logo-ok', 'logo-ok', 'Logo OK', '#000000', 'https://cdn.example.sa/logo.svg')
            """, connection, transaction);

        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);

        await transaction.RollbackAsync(Ct);
    }

    private async Task ExecuteAsOwnerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
