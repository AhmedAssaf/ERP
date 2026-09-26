using System.Net;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// QA pass, tenant isolation invariant on the admin slice (F-02, F-06, F-07): every tenant administration endpoint refuses
/// an anonymous request and a signed-in administrator of another tenant, changes nothing for them, and never shows one
/// tenant's members or branding on another tenant's host. The logo path does not exist on the platform host.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdminIsolationTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(AdminRequests.Pages), MemberType = typeof(AdminRequests))]
    public async Task An_anonymous_request_for_an_admin_page_is_challenged(string path)
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.GetAsync(client, path, user: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_anonymous_logo_upload_is_challenged_and_changes_nothing()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.PostLogoAsync(factory, client, user: null, AdminRequests.Png(), "image/png", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(AdminRequests.Pages), MemberType = typeof(AdminRequests))]
    public async Task An_admin_of_another_tenant_is_forbidden_on_the_admin_page_and_audited(string path)
    {
        var target = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var (_, intruder) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(target));

        using var response = await AdminRequests.GetAsync(client, path, intruder, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, target.TenantId, intruder.Subject, "identity.cross_tenant_denied", Ct))
            .ShouldBe(1);
    }

    [Fact]
    public async Task An_admin_of_another_tenant_cannot_upload_a_logo_even_with_a_valid_antiforgery_token()
    {
        var target = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var (_, intruder) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(target));

        using var response = await AdminRequests.PostLogoAsync(factory, client, intruder, AdminRequests.Png(), "image/png", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await TenantRows.BrandingAsync(db.AppConnectionString, target, Ct)).LogoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task A_user_in_two_organizations_administers_only_the_tenant_where_they_hold_the_admin_role()
    {
        var administered = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var evaluated = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var user = new TestUser($"qa-both-{Guid.NewGuid():N}", [administered.KeycloakOrgAlias, evaluated.KeycloakOrgAlias], "en");
        await MemberRows.InsertAsync(db.AppConnectionString, administered.TenantId, user.Subject, $"{user.Subject}@a.example.sa", [TenantRoles.TenantAdmin], "active", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, evaluated.TenantId, user.Subject, $"{user.Subject}@e.example.sa", [TenantRoles.TechnicalEvaluator], "active", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var own = AdminRequests.Client(factory, TenantRows.Host(administered));
        using var other = AdminRequests.Client(factory, TenantRows.Host(evaluated));

        using var allowed = await AdminRequests.GetAsync(own, AdminRequests.StaffPath, user, Ct);
        using var refused = await AdminRequests.GetAsync(other, AdminRequests.StaffPath, user, Ct);
        using var upload = await AdminRequests.PostLogoAsync(factory, other, user, AdminRequests.Png(), "image/png", Ct);

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        upload.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await TenantRows.BrandingAsync(db.AppConnectionString, evaluated, Ct)).LogoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task The_staff_page_never_lists_a_member_of_another_tenant()
    {
        var (tenant, admin) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        var other = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var foreignEmail = $"qa-foreign-{Guid.NewGuid():N}@other.example.sa";
        var ownEmail = $"qa-own-{Guid.NewGuid():N}@own.example.sa";
        await MemberRows.InsertAsync(db.AppConnectionString, other.TenantId, $"user-{Guid.NewGuid():N}", foreignEmail, [TenantRoles.FinanceApprover], "active", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, $"user-{Guid.NewGuid():N}", ownEmail, [TenantRoles.FinanceApprover], "active", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.GetAsync(client, AdminRequests.StaffPath, admin, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain(ownEmail);
        html.ShouldNotContain(foreignEmail);
    }

    [Fact]
    public async Task The_branding_page_shows_the_host_tenants_branding_and_no_other()
    {
        var (tenant, admin) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        var other = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.GetAsync(client, AdminRequests.BrandingPath, admin, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain(tenant.Branding.PortalName);
        html.ShouldNotContain(other.Branding.PortalName);
    }

    [Theory]
    [InlineData("/admin/staff")]
    [InlineData("/admin/branding")]
    [InlineData("/branding/logo/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.png")]
    public async Task A_tenant_admin_path_on_the_platform_host_is_404_even_for_a_tenant_admin(string path)
    {
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, PlatformWebFactory.PlatformHost);

        using var response = await AdminRequests.GetAsync(client, path, TestUser.AcmeAdmin, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_logo_upload_on_the_platform_host_is_404_and_changes_no_tenant()
    {
        var (tenant, admin) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, PlatformWebFactory.PlatformHost);

        using var response = await AdminRequests.PostLogoAsync(factory, client, admin, AdminRequests.Png(), "image/png", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_host_is_404_for_the_admin_pages_and_the_logo_upload()
    {
        var (_, admin) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, $"qa-{Guid.NewGuid():N}.example.invalid");

        using var staff = await AdminRequests.GetAsync(client, AdminRequests.StaffPath, admin, Ct);
        using var upload = await AdminRequests.PostLogoAsync(factory, client, admin, AdminRequests.Png(), "image/png", Ct);

        staff.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        upload.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
