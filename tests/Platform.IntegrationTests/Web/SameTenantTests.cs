using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit;

namespace Platform.IntegrationTests.Web;

[Collection(DatabaseCollection.Name)]
public class SameTenantTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_request_is_challenged()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Member_of_the_hosts_organization_gets_the_page()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/").As(TestUser.AcmeAdmin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Token_for_another_tenant_is_forbidden_and_audited()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/").As(TestUser.BetaAdmin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme);
        await using var audit = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuditDbContext>>().CreateDbContextAsync(Ct);
        var denied = await audit.Events.Where(e => e.Action == "identity.cross_tenant_denied" && e.ActorId == "beta.admin").ToListAsync(Ct);
        denied.ShouldNotBeEmpty();
        denied.ShouldAllBe(e => e.SubjectType == "host" && e.SubjectId == "acme.localhost");
        denied.ShouldAllBe(e => e.Data == "{}");
    }

    [Fact]
    public async Task Endpoint_with_require_authorization_forbids_a_user_of_another_tenant()
    {
        using var response = await GetAuthorizedEndpointAsync("acme.localhost", TestUser.BetaAdmin);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Endpoint_with_require_authorization_serves_a_member_of_the_hosts_organization()
    {
        using var response = await GetAuthorizedEndpointAsync("acme.localhost", TestUser.AcmeAdmin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_cookie_has_a_fixed_thirty_minute_lifetime_until_membership_is_revalidated()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);

        var cookie = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        cookie.ExpireTimeSpan.ShouldBe(TimeSpan.FromMinutes(30));
        cookie.SlidingExpiration.ShouldBeFalse();
    }

    private async Task<HttpResponseMessage> GetAuthorizedEndpointAsync(string host, TestUser user)
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        await using var withEndpoint = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, AuthorizedEndpointStartupFilter>()));
        using var client = withEndpoint.CreateClient(new() { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });

        return await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, AuthorizedEndpointStartupFilter.Path).As(user), Ct);
    }
}
