using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;

namespace Platform.IntegrationTests.Tenancy;

/// <summary>F-54: the platform console reads every tenant through tenancy.list_tenants(), never the table itself.</summary>
[Collection(DatabaseCollection.Name)]
public class TenantCatalogTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_catalog_lists_every_tenant_with_slug_alias_name_and_status()
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.PlatformScope();

        var tenants = await scope.ServiceProvider.GetRequiredService<ITenantCatalog>().ListAsync(Ct);

        tenants.ShouldContain(new TenantSummary(TestTenants.Acme.TenantId, "acme", "acme", "Acme Contracting", "active"));
        tenants.ShouldContain(new TenantSummary(TestTenants.Beta.TenantId, "beta", "beta", "Beta Industries", "active"));
    }

    [Fact]
    public async Task The_catalog_refuses_a_scope_that_is_not_a_platform_request()
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var tenantScope = host.ScopeFor(TestTenants.Acme);
        await using var bareScope = host.ScopeFor(null);

        await Should.ThrowAsync<InvalidOperationException>(() => tenantScope.ServiceProvider.GetRequiredService<ITenantCatalog>().ListAsync(Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => bareScope.ServiceProvider.GetRequiredService<ITenantCatalog>().ListAsync(Ct));
    }

    [Fact]
    public async Task The_list_function_documents_that_it_is_gated_in_code()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select obj_description('tenancy.list_tenants()'::regprocedure, 'pg_proc')", connection);

        var comment = (string?)await command.ExecuteScalarAsync(Ct);

        comment.ShouldNotBeNull();
        comment.ShouldContain("gated in application code");
    }

    [Fact]
    public async Task The_app_role_still_cannot_read_the_tenants_table()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select count(*) from tenancy.tenants", connection);

        var refused = await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct));

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }
}
