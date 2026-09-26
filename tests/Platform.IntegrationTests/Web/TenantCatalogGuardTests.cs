using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;
using Platform.Web.PlatformHost;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// <c>tenancy.list_tenants()</c> is executable by the shared <c>erp_app</c> role, so the database cannot keep a tenant
/// request from enumerating tenants; <see cref="ITenantCatalog"/> refuses in code unless the request is the platform
/// console's (review of plan task 7). Both endpoints below resolve the catalog from the request's own scope.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenantCatalogGuardTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_catalog_refuses_inside_a_tenant_host_request()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, CatalogEndpointStartupFilter.TenantPath).As(TestUser.AcmeAdmin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the tenant user passed authorization; only the catalog refuses");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe(CatalogEndpointStartupFilter.Refused);
    }

    [Fact]
    public async Task The_catalog_lists_tenants_inside_a_platform_host_request()
    {
        await using var factory = Factory();
        var admin = AuthCookies.Principal(
            [new Claim("sub", "platform.admin"), new Claim("preferred_username", "platform.admin"), new Claim("acr", "2"), new Claim("roles", "platform-admin")]);
        var cookie = AuthCookies.Protect(factory.Services, PlatformAuthentication.CookieScheme, admin);
        using var client = factory.CreateClient(new() { BaseAddress = new Uri($"http://{PlatformWebFactory.PlatformHost}"), AllowAutoRedirect = false, HandleCookies = false });

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, CatalogEndpointStartupFilter.PlatformPath).WithCookie(PlatformAuthentication.CookieName, cookie), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        int.Parse(await response.Content.ReadAsStringAsync(Ct), CultureInfo.InvariantCulture).ShouldBeGreaterThanOrEqualTo(2);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory() =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, CatalogEndpointStartupFilter>()));

    /// <summary>The same catalog call on a tenant path and on a platform path; a refusal answers <see cref="Refused"/>.</summary>
    private sealed class CatalogEndpointStartupFilter : IStartupFilter
    {
        public const string TenantPath = "/test/catalog";
        public const string PlatformPath = "/platform/test/catalog";
        public const string Refused = "refused";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = app.Properties.TryGetValue("__EndpointRouteBuilder", out var value) && value is IEndpointRouteBuilder routeBuilder
                ? routeBuilder
                : throw new InvalidOperationException("The host did not expose its endpoint route builder.");
            endpoints.MapGet(TenantPath, ListAsync);
            endpoints.MapGet(PlatformPath, ListAsync);
        };

        private static async Task<IResult> ListAsync(ITenantCatalog catalog, CancellationToken cancellationToken)
        {
            try
            {
                var tenants = await catalog.ListAsync(cancellationToken);
                return Results.Text(tenants.Count.ToString(CultureInfo.InvariantCulture));
            }
            catch (InvalidOperationException)
            {
                return Results.Text(Refused);
            }
        }
    }
}
