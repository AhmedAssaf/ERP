using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.PlatformHost;
using Platform.Web.Tenancy;

namespace Platform.IntegrationTests.Web;

public class TenantCircuitHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Circuit_start_sets_the_tenant_from_the_connection_host()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://acme.localhost:8443/"), ConnectionFrom("acme.localhost"), new TwoTenantDirectory(), accessor);

        await handler.OnCircuitOpenedAsync(null!, Ct);

        accessor.Current.ShouldBe(TestTenants.Acme);
    }

    [Fact]
    public async Task Circuit_start_accepts_a_base_uri_host_that_differs_only_in_case()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://ACME.localhost:8443/"), ConnectionFrom("acme.localhost"), new TwoTenantDirectory(), accessor);

        await handler.OnCircuitOpenedAsync(null!, Ct);

        accessor.Current.ShouldBe(TestTenants.Acme);
    }

    [Fact]
    public async Task Circuit_start_refuses_a_base_uri_for_another_host()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://beta.localhost:8443/"), ConnectionFrom("acme.localhost"), new TwoTenantDirectory(), accessor);

        await Should.ThrowAsync<InvalidOperationException>(() => handler.OnCircuitOpenedAsync(null!, Ct));

        accessor.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Circuit_start_refuses_when_there_is_no_connection()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://acme.localhost:8443/"), new HttpContextAccessor(), new TwoTenantDirectory(), accessor);

        await Should.ThrowAsync<InvalidOperationException>(() => handler.OnCircuitOpenedAsync(null!, Ct));

        accessor.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Circuit_start_refuses_a_host_no_tenant_owns()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://nobody.localhost:8443/"), ConnectionFrom("nobody.localhost"), new TwoTenantDirectory(), accessor);

        await Should.ThrowAsync<InvalidOperationException>(() => handler.OnCircuitOpenedAsync(null!, Ct));

        accessor.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Circuit_on_the_platform_host_has_no_tenant()
    {
        var accessor = new TenantAccessor();
        var connection = ConnectionFrom("platform.localhost");
        PlatformRequest.Mark(connection.HttpContext!);
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://platform.localhost:8443/"), connection, new TwoTenantDirectory(), accessor);

        await handler.OnCircuitOpenedAsync(null!, Ct);

        accessor.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Platform_circuit_still_refuses_a_base_uri_for_another_host()
    {
        var accessor = new TenantAccessor();
        var connection = ConnectionFrom("platform.localhost");
        PlatformRequest.Mark(connection.HttpContext!);
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://acme.localhost:8443/"), connection, new TwoTenantDirectory(), accessor);

        await Should.ThrowAsync<InvalidOperationException>(() => handler.OnCircuitOpenedAsync(null!, Ct));

        accessor.Current.ShouldBeNull();
    }

    [Fact]
    public void Runs_before_any_other_circuit_handler()
    {
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://acme.localhost:8443/"), new HttpContextAccessor(), new TwoTenantDirectory(), new TenantAccessor());

        handler.Order.ShouldBe(int.MinValue);
    }

    private static HttpContextAccessor ConnectionFrom(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host, 8443);
        return new HttpContextAccessor { HttpContext = context };
    }

    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation(string baseUri) => Initialize(baseUri, baseUri);
    }

    private sealed class TwoTenantDirectory : ITenantDirectory
    {
        public Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(host switch { "acme.localhost" => TestTenants.Acme, "beta.localhost" => TestTenants.Beta, _ => null });

        public void Invalidate(string host)
        {
        }
    }
}
