using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// The staff view of vendors and the join page (vendor plan task 5, F-10 as narrowed, V-7, V-11): <c>/admin/vendors</c>
/// and <c>/admin/vendors/{id}</c> open to a contracts officer or tenant admin only, list the tenant's related vendors with
/// a pending or approved filter, and show the card and documents; the admin navigation links to them for those roles.
/// <c>/vendor/join</c> lets a signed-in vendor of another tenant work with this one, then asks it to sign in again.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class VendorStaffPagesTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_technical_evaluator_cannot_open_the_vendor_pages()
    {
        var (companyId, _) = await VendorAsync("Evaluator Hidden Company");
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        foreach (var role in new[] { TenantRoles.TechnicalEvaluator, TenantRoles.FinanceApprover })
        {
            var user = await AdminRequests.MemberAsync(db, TestTenants.Acme, [role], "en", Ct);
            foreach (var path in new[] { "/admin/vendors", $"/admin/vendors/{companyId}" })
            {
                using var response = await AdminRequests.GetAsync(client, path, user, Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{role} {path}");
            }
        }
    }

    [Theory]
    [InlineData(TenantRoles.ContractsOfficer)]
    [InlineData(TenantRoles.TenantAdmin)]
    public async Task An_officer_or_admin_lists_filters_and_opens_the_tenants_vendors(string role)
    {
        var (pendingId, _) = await VendorAsync("Pending Page Company");
        var (approvedId, _) = await VendorAsync("Approved Page Company");
        await ApproveAsync(approvedId);
        var (betaOnlyId, _) = await VendorAsync("Beta Only Company", TestTenants.Beta);
        var user = await AdminRequests.MemberAsync(db, TestTenants.Acme, [role], "en", Ct);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        var all = await PageAsync(client, "/admin/vendors", user);
        all.ShouldContain($"data-vendor=\"{pendingId}\"");
        all.ShouldContain($"data-vendor=\"{approvedId}\"");
        all.ShouldNotContain($"data-vendor=\"{betaOnlyId}\"");
        all.ShouldContain("Pending Page Company");
        all.ShouldContain($"data-vendor-status=\"{pendingId}:pending\"");
        all.ShouldContain($"data-vendor-status=\"{approvedId}:approved\"");
        all.ShouldContain($"data-vendor-documents=\"{pendingId}:blocking\"");
        // The admin navigation links to the vendors for these roles.
        all.ShouldContain("href=\"admin/vendors\"");

        var pending = await PageAsync(client, "/admin/vendors?status=pending", user);
        pending.ShouldContain($"data-vendor=\"{pendingId}\"");
        pending.ShouldNotContain($"data-vendor=\"{approvedId}\"");
        var approved = await PageAsync(client, "/admin/vendors?status=approved", user);
        approved.ShouldContain($"data-vendor=\"{approvedId}\"");
        approved.ShouldNotContain($"data-vendor=\"{pendingId}\"");

        var card = await PageAsync(client, $"/admin/vendors/{pendingId}", user);
        card.ShouldContain($"data-vendor-card=\"{pendingId}\"");
        card.ShouldContain("Pending Page Company");
        card.ShouldContain("contact@example.test");
        card.ShouldContain("data-approve");

        // Not related to acme: the page says it is not found and shows nothing of the company.
        var other = await PageAsync(client, $"/admin/vendors/{betaOnlyId}", user);
        other.ShouldContain("data-vendor-not-found");
        other.ShouldNotContain("Beta Only Company");
    }

    [Fact]
    public async Task The_admin_navigation_shows_vendors_to_an_officer_and_staff_only_to_an_admin()
    {
        var officer = await AdminRequests.MemberAsync(db, TestTenants.Acme, [TenantRoles.ContractsOfficer], "en", Ct);
        var admin = await AdminRequests.MemberAsync(db, TestTenants.Acme, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        (await PageAsync(client, "/admin/vendors", officer)).ShouldNotContain("href=\"admin/staff\"");
        (await PageAsync(client, "/admin/staff", admin)).ShouldContain("href=\"admin/vendors\"");
        // Working out the links refuses nobody, so an officer's page view writes no role denial.
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, officer.Subject, "identity.role_denied", Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData("ar")]
    [InlineData("en")]
    public async Task The_vendor_pages_for_staff_render_in_the_users_language_without_raw_keys(string locale)
    {
        var (companyId, _) = await VendorAsync("Localized Staff Company");
        var user = await AdminRequests.MemberAsync(db, TestTenants.Acme, [TenantRoles.ContractsOfficer], locale, Ct);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        foreach (var path in new[] { "/admin/vendors", $"/admin/vendors/{companyId}" })
        {
            var html = await PageAsync(client, path, user);
            html.ShouldContain(locale == "ar" ? "dir=\"rtl\"" : "dir=\"ltr\"", Case.Sensitive, path);
            RawKey().IsMatch(html).ShouldBeFalse(path);
        }
    }

    [Fact]
    public async Task A_vendor_of_another_tenant_joins_through_the_join_page()
    {
        var (companyId, userId) = await VendorAsync("Join Page Company");
        var vendor = Vendor(userId);
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]) };
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts))));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://beta.localhost"), AllowAutoRedirect = false });

        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/join").As(vendor), Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync(Ct);
        WebUtility.HtmlDecode(html).ShouldContain("Work with Beta Industries");

        using var joined = await PostJoinAsync(client, vendor, html);

        var result = await joined.Content.ReadAsStringAsync(Ct);
        result.ShouldContain("data-vendor-joined");
        result.ShouldContain("action=\"/account/sign-out\"");
        result.ShouldContain("name=\"returnUrl\" value=\"/vendor\"");
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Beta.TenantId].ShouldBe("pending");
        accounts.Steps.ShouldContain("add-organization");
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_join_page_is_closed_to_staff_and_tells_a_related_vendor_it_already_works_there()
    {
        var (_, userId) = await VendorAsync("Already Related Company");
        var staff = await AdminRequests.MemberAsync(db, TestTenants.Beta, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);

        using (var beta = factory.ClientFor("beta.localhost"))
        {
            using var refused = await beta.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/join").As(staff), Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var acme = factory.ClientFor("acme.localhost");
        using var already = await acme.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/join").As(Vendor(userId)), Ct);
        already.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await already.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("data-vendor-already-joined");
        // The button stays: a user who lost the organization membership gets it back from here.
        html.ShouldContain("data-vendor-join-form");
    }

    [Fact]
    public async Task A_related_vendor_that_lost_its_membership_restores_it_from_the_join_page()
    {
        var (companyId, userId) = await VendorAsync("Lost Membership Company");
        // Related to acme, but the token (and Keycloak) no longer carry acme's organization.
        var vendor = Vendor(userId) with { Organizations = [] };
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: []) };
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts))));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });

        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/join").As(vendor), Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("data-vendor-already-joined");

        using var joined = await PostJoinAsync(client, vendor, html);

        (await joined.Content.ReadAsStringAsync(Ct)).ShouldContain("data-vendor-joined");
        accounts.Steps.ShouldContain("add-organization");
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).Keys.ShouldBe([TestTenants.Acme.TenantId]);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, userId, "vendor.membership_restored", Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_vendor_of_another_tenant_opening_the_vendor_home_or_the_tenant_home_is_sent_to_the_join_page()
    {
        var (_, userId) = await VendorAsync("Redirected Vendor Company");
        var vendor = Vendor(userId);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var beta = factory.ClientFor("beta.localhost");

        foreach (var path in new[] { "/vendor", "/" })
        {
            using var response = await beta.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).As(vendor), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Redirect, path);
            response.Headers.Location!.OriginalString.ShouldBe("/vendor/join", path);
        }

        // At its own tenant the vendor home opens as before.
        using var acme = factory.ClientFor("acme.localhost");
        using var home = await acme.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor").As(vendor), Ct);
        home.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<string> PageAsync(HttpClient client, string path, TestUser user)
    {
        using var response = await AdminRequests.GetAsync(client, path, user, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    private static Task<HttpResponseMessage> PostJoinAsync(HttpClient client, TestUser user, string page)
    {
        var form = JoinForm().Match(page);
        form.Success.ShouldBeTrue("the page renders the join form");
        var fields = HiddenInputs().Matches(form.Value)
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
        var request = new HttpRequestMessage(HttpMethod.Post, "/vendor/join") { Content = new FormUrlEncodedContent(fields) };
        return client.SendAsync(request.As(user), Ct);
    }

    private async Task ApproveAsync(Guid companyId)
    {
        // W-33: approval needs a verified owner; these tests are about approval itself (CrOwnershipTests covers the check).
        await OwnershipRows.VerifyAsOwnerAsync(db.OwnerConnectionString, companyId, TestTenants.Acme.TenantId, Ct);
        var officer = $"officer-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, officer, $"{officer}@acme.test", [TenantRoles.ContractsOfficer], "active", Ct);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
        (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, officer, Ct)).IsSuccess.ShouldBeTrue();
    }

    private async Task<(Guid CompanyId, string UserId)> VendorAsync(string nameEn, Platform.Shared.Tenancy.TenantContext? tenant = null)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, tenant ?? TestTenants.Acme, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        return (companyId, userId);
    }

    private static TestUser Vendor(string userId) =>
        new(userId, ["acme"], "en", RealmRoles: [IdentityClaims.VendorRealmRole], Email: $"{userId}@vendor.test", EmailVerified: true);

    [GeneratedRegex("<form\\b[^>]*data-vendor-join-form[^>]*>.*?</form>", RegexOptions.Singleline)]
    private static partial Regex JoinForm();

    [GeneratedRegex("<input\\b(?=[^>]*type=\"hidden\")(?=[^>]*name=\"(?<name>[^\"]*)\")(?=[^>]*value=\"(?<value>[^\"]*)\")[^>]*>")]
    private static partial Regex HiddenInputs();

    // A shared resource key rendered as itself (Vendor.* or Admin.*), as a missing key would render.
    [GeneratedRegex(@"\b(Vendor|Admin)\.[A-Z][A-Za-z]+\.[A-Za-z.]+")]
    private static partial Regex RawKey();
}
