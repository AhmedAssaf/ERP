using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Shared.Tenancy;
using Platform.Web.Account;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// The scope narrowing on its own, without Keycloak: a tenant host asks for its own organization only, and a challenge
/// with no tenant asks for no organization at all (the bare scope would bring back the picker that lists every tenant a
/// user belongs to).
/// </summary>
public class TenantOrganizationScopeTests
{
    [Fact]
    public async Task A_tenant_host_replaces_the_bare_scope_with_its_own_organization()
    {
        var accessor = new TenantAccessor();
        accessor.Set(new TenantContext(Guid.NewGuid(), "acme", "acme", "ar", new TenantBranding("Acme", "#0F766E", null)));
        var context = RedirectFor(accessor);

        await TenantOrganizationScope.Apply(context);

        context.ProtocolMessage.Scope.ShouldBe("openid profile organization:acme email");
    }

    [Fact]
    public async Task A_challenge_with_no_tenant_drops_the_bare_organization_scope()
    {
        var context = RedirectFor(new TenantAccessor());

        await TenantOrganizationScope.Apply(context);

        context.ProtocolMessage.Scope.ShouldBe("openid profile email");
    }

    [Fact]
    public async Task A_challenge_with_no_tenant_accessor_drops_the_bare_organization_scope()
    {
        var context = RedirectFor(accessor: null);

        await TenantOrganizationScope.Apply(context);

        context.ProtocolMessage.Scope.ShouldBe("openid profile email");
    }

    private static RedirectContext RedirectFor(ITenantAccessor? accessor)
    {
        var services = new ServiceCollection();
        if (accessor is not null)
        {
            services.AddSingleton(accessor);
        }

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var scheme = new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler));
        return new RedirectContext(http, scheme, new OpenIdConnectOptions(), new AuthenticationProperties())
        {
            ProtocolMessage = new OpenIdConnectMessage { Scope = "openid profile organization email" },
        };
    }
}
