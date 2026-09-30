using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;

namespace Platform.IntegrationTests.Tenancy;

/// <summary>
/// W-10 (spec 6.4): the worker's usage job labels its counts with tenant slugs, and <see cref="ITenantCatalog"/> is for the
/// platform console only (the worker never marks its scope as a platform request). <see cref="ITenantSlugs"/> gives slugs
/// only, to a scope with neither a tenant nor a vendor context, as the database's own rule for the platform's sessions is.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenantSlugsTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scope_without_a_context_gets_every_tenant_slug_by_id()
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(null);

        var slugs = await scope.ServiceProvider.GetRequiredService<ITenantSlugs>().ListAsync(Ct);

        slugs[TestTenants.Acme.TenantId].ShouldBe("acme");
        slugs[TestTenants.Beta.TenantId].ShouldBe("beta");
    }

    [Fact]
    public async Task A_tenant_or_vendor_scope_is_refused()
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var tenantScope = host.ScopeFor(TestTenants.Acme);
        await using var vendorScope = host.ScopeFor(null, vendorCompanyId: Guid.NewGuid());

        await Should.ThrowAsync<InvalidOperationException>(() => tenantScope.ServiceProvider.GetRequiredService<ITenantSlugs>().ListAsync(Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => vendorScope.ServiceProvider.GetRequiredService<ITenantSlugs>().ListAsync(Ct));
    }
}
