using System.Net;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// QA pass, F-07 role matrix on the admin slice: each of the four MVP tenant roles against each tenant administration
/// endpoint. Only <c>tenant-admin</c> opens <c>/admin/staff</c> and <c>/admin/branding</c> or uploads a logo; the other
/// three roles, all three together, and a member of the organization without a member row are refused with 403, and a
/// refused upload changes nothing.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdminRoleMatrixTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, HttpStatusCode> PageMatrix()
    {
        var data = new TheoryData<string, string, HttpStatusCode>();
        foreach (var path in new[] { AdminRequests.StaffPath, AdminRequests.BrandingPath })
        {
            foreach (var role in TenantRoles.All)
            {
                data.Add(path, role, role == TenantRoles.TenantAdmin ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
            }
        }

        return data;
    }

    public static TheoryData<string, HttpStatusCode> UploadMatrix()
    {
        var data = new TheoryData<string, HttpStatusCode>();
        foreach (var role in TenantRoles.All)
        {
            data.Add(role, role == TenantRoles.TenantAdmin ? HttpStatusCode.SeeOther : HttpStatusCode.Forbidden);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PageMatrix))]
    public async Task Each_role_opens_an_admin_page_only_when_it_is_tenant_admin(string path, string role, HttpStatusCode expected)
    {
        var (tenant, user) = await AdminRequests.TenantWithMemberAsync(db, [role], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.GetAsync(client, path, user, Ct);

        response.StatusCode.ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(UploadMatrix))]
    public async Task Each_role_uploads_a_logo_only_when_it_is_tenant_admin(string role, HttpStatusCode expected)
    {
        var (tenant, user) = await AdminRequests.TenantWithMemberAsync(db, [role], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.PostLogoAsync(factory, client, user, AdminRequests.Png(), "image/png", Ct);

        response.StatusCode.ShouldBe(expected);
        var logo = (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl;
        if (expected == HttpStatusCode.SeeOther)
        {
            logo.ShouldNotBeNull();
        }
        else
        {
            logo.ShouldBeNull();
        }
    }

    [Theory]
    [MemberData(nameof(AdminRequests.Pages), MemberType = typeof(AdminRequests))]
    public async Task All_three_non_admin_roles_together_do_not_open_an_admin_page(string path)
    {
        var (tenant, user) = await AdminRequests.TenantWithMemberAsync(
            db, [.. TenantRoles.All.Where(r => r != TenantRoles.TenantAdmin)], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.GetAsync(client, path, user, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(AdminRequests.Pages), MemberType = typeof(AdminRequests))]
    public async Task A_member_of_the_organization_without_a_member_row_does_not_open_an_admin_page(string path)
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var user = await AdminRequests.MemberAsync(db, tenant, roles: [], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.GetAsync(client, path, user, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
