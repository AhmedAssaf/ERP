using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
}
