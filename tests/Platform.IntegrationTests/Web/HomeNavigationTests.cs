using System.Net;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// QA fix, commit 2: the admin navigation on the tenant's home page and inside the admin layout shows the Staff and
/// Branding links only to a tenant admin (the QA observation was that no page linked to them at all, so nothing proved
/// an evaluator without the role could not reach them). Checked in both languages, since a missing translation could
/// otherwise hide a link that should have shown, or leave a stray key visible where nothing should show at all.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HomeNavigationTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Locales() => new("ar", "en");

    public static TheoryData<string, string> NonAdminRoleAndLocale()
    {
        var data = new TheoryData<string, string>();
        foreach (var locale in new[] { "ar", "en" })
        {
            foreach (var role in TenantRoles.All.Where(r => r != TenantRoles.TenantAdmin))
            {
                data.Add(role, locale);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Locales))]
    public async Task A_tenant_admin_sees_both_admin_links_on_home(string locale)
    {
        var html = await HomeAsync([TenantRoles.TenantAdmin], locale);

        html.ShouldContain("admin/staff");
        html.ShouldContain("admin/branding");
    }

    [Theory]
    [MemberData(nameof(NonAdminRoleAndLocale))]
    public async Task A_non_admin_role_sees_no_admin_link_on_home(string role, string locale)
    {
        var html = await HomeAsync([role], locale);

        html.ShouldNotContain("admin/staff");
        html.ShouldNotContain("admin/branding");
    }

    [Theory]
    [MemberData(nameof(Locales))]
    public async Task All_three_non_admin_roles_together_see_no_admin_link_on_home(string locale)
    {
        var html = await HomeAsync([.. TenantRoles.All.Where(r => r != TenantRoles.TenantAdmin)], locale);

        html.ShouldNotContain("admin/staff");
        html.ShouldNotContain("admin/branding");
    }

    [Theory]
    [MemberData(nameof(Locales))]
    public async Task A_member_of_the_organization_without_a_member_row_sees_no_admin_link_on_home(string locale)
    {
        var html = await HomeAsync([], locale);

        html.ShouldNotContain("admin/staff");
        html.ShouldNotContain("admin/branding");
    }

    [Theory]
    [MemberData(nameof(Locales))]
    public async Task A_tenant_admin_sees_both_admin_links_inside_the_admin_layout(string locale)
    {
        var (tenant, user) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], locale, Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.GetAsync(client, AdminRequests.StaffPath, user, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("admin/staff");
        html.ShouldContain("admin/branding");
    }

    [Fact]
    public async Task A_signed_in_vendor_opening_home_is_sent_to_the_vendor_home()
    {
        var (tenant, member) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        // A vendor of the tenant, and a member row holder who also carries the vendor role: neither may see the staff home.
        var vendor = new TestUser($"vendor-{Guid.NewGuid():N}", [tenant.KeycloakOrgAlias], "en", RealmRoles: [IdentityClaims.VendorRealmRole]);
        var both = member with { RealmRoles = [IdentityClaims.VendorRealmRole] };
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        foreach (var user in new[] { vendor, both })
        {
            using var response = await AdminRequests.GetAsync(client, "/", user, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            response.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe("/vendor");
        }

        using var staff = await AdminRequests.GetAsync(client, "/", member, Ct);
        staff.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<string> HomeAsync(string[] roles, string locale)
    {
        var (tenant, user) = await AdminRequests.TenantWithMemberAsync(db, roles, locale, Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.GetAsync(client, "/", user, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(Ct);
    }
}
