using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;

namespace Platform.IntegrationTests.Tenancy;

[Collection(DatabaseCollection.Name)]
public class TenantDirectoryTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact]
    public async Task The_app_role_cannot_read_the_tenant_tables_directly()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select count(*) from tenancy.tenants", connection);

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }
}
