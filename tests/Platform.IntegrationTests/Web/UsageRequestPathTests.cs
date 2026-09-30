using System.Net;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Web.Account;
using Platform.Web.Tenancy;
using Platform.Web.Usage;
using Platform.Web.Vendor;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-10 usage metrics through the web host's own pipeline (spec 6.2 to 6.4; O-20, O-21, O-24), QA additions: a vendor's
/// request counts as vendor activity only when the vendor context set by <c>VendorContextMiddleware</c> passed the Vendor
/// policy for the host tenant, which <c>UserActivityMiddleware</c> sees only because it runs after it; a vendor on the
/// join page of a tenant it has not joined, and an applicant before its company exists, leave no activity (the row-level
/// security would admit the vendor's row, so the classifier is the only wall); and the circuit handler classifies only
/// after the handlers that set the tenant, the vendor context and the session have run. Each test uses tenants of its own.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class UsageRequestPathTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_vendor_request_counts_the_vendor_as_active_for_its_tenant()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var vendor = await VendorOfAsync(tenant, "Usage Vendor Trading");
        await using var factory = Factory(tenant.KeycloakOrgAlias);
        using var client = Client(factory, tenant);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor").As(vendor), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var row = (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).ShouldHaveSingleItem();
        (row.UserId, row.Kind).ShouldBe((vendor.Subject, "vendor"));
    }

    [Fact]
    public async Task A_vendor_on_the_join_page_of_a_tenant_it_has_not_joined_records_no_activity()
    {
        var home = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var other = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var vendor = await VendorOfAsync(home, "Usage Join Supplies");
        await using var factory = Factory(home.KeycloakOrgAlias);
        using var client = Client(factory, other);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/join").As(vendor), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the join page opens under the JoiningVendor policy");
        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, other.TenantId, Ct)).ShouldBeEmpty();
        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, home.TenantId, Ct)).ShouldBeEmpty("a request on another host is not activity at home");
    }

    [Fact]
    public async Task An_applicant_before_its_company_exists_records_no_activity()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var subject = Guid.NewGuid().ToString();
        var applicant = new TestUser(subject, [], "en", Email: $"{subject}@applicant.test", EmailVerified: true);
        await using var factory = Factory();
        using var client = Client(factory, tenant);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/register/company").As(applicant), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ActivityRows.ForTenantAsync(db.OwnerConnectionString, tenant.TenantId, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_usage_circuit_handler_classifies_after_the_tenant_vendor_and_session_handlers()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();

        // The framework runs circuit handlers in ascending Order.
        var handlers = scope.ServiceProvider.GetServices<CircuitHandler>().OrderBy(h => h.Order).ToList();
        var usage = handlers.FindIndex(h => h is UsageCircuitHandler);

        usage.ShouldBeGreaterThan(handlers.FindIndex(h => h is TenantCircuitHandler), "the tenant is set first");
        usage.ShouldBeGreaterThan(handlers.FindIndex(h => h is VendorCircuitHandler), "the vendor context is set first");
        usage.ShouldBeGreaterThan(handlers.FindIndex(h => h is CircuitSessionGuard), "the session is checked first");
        handlers.Where(h => h is not UsageCircuitHandler).ShouldAllBe(h => h.Order != handlers[usage].Order, "no tie leaves the order to registration");
    }

    /// <summary>A web host whose Keycloak side of vendor accounts holds the user in <paramref name="organizations"/>, with the vendor role if any.</summary>
    private WebApplicationFactory<Program> Factory(params string[] organizations)
    {
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: organizations.Length > 0, OrganizationAliases: organizations) };
        return new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts))));
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, Platform.Shared.Tenancy.TenantContext tenant) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{TenantRows.Host(tenant)}"), AllowAutoRedirect = false });

    /// <summary>A vendor user whose company is registered with <paramref name="tenant"/>, with the claims the vendor realm gives it.</summary>
    private async Task<TestUser> VendorOfAsync(Platform.Shared.Tenancy.TenantContext tenant, string nameEn)
    {
        var subject = Guid.NewGuid().ToString();
        await VendorRows.RegisterAsync(db.AppConnectionString, tenant, subject, VendorRows.NewCrNumber(), nameEn, Ct);
        return new TestUser(subject, [tenant.KeycloakOrgAlias], "en", RealmRoles: [IdentityClaims.VendorRealmRole], Email: $"{subject}@vendor.test", EmailVerified: true);
    }
}
