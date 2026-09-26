using Microsoft.AspNetCore.Components;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.Tenancy;

namespace Platform.IntegrationTests.Web;

public class TenantCircuitHandlerTests
{
    [Fact]
    public async Task Circuit_start_sets_the_tenant_from_the_base_uri()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(new FixedNavigation("https://acme.localhost:8443/"), new OneTenantDirectory(), accessor);

        await handler.OnCircuitOpenedAsync(null!, TestContext.Current.CancellationToken);

        accessor.Current.ShouldBe(TestTenants.Acme);
    }

    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation(string baseUri) => Initialize(baseUri, baseUri);
    }

    private sealed class OneTenantDirectory : ITenantDirectory
    {
        public Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(host == "acme.localhost" ? TestTenants.Acme : null);
    }
}
