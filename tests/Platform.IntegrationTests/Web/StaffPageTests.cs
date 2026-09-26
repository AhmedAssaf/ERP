using System.Net;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// <c>/admin/staff</c> (F-06, spec 4.2) through the web host with the header-driven test sign-in: tenant admins only,
/// listing the tenant's members with their roles and status. Keycloak is not contacted to render the list.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StaffPageTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_non_admin_cannot_open_the_staff_page()
    {
        var evaluator = new TestUser($"evaluator-{Guid.NewGuid():N}", ["acme"]);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, evaluator.Subject, $"{evaluator.Subject}@acme.test", [TenantRoles.TechnicalEvaluator], "active", Ct);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/staff").As(evaluator), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_tenant_admin_sees_the_members_with_their_roles_and_status()
    {
        var admin = new TestUser($"admin-{Guid.NewGuid():N}", ["acme"], "en");
        var invitedEmail = $"invited-{Guid.NewGuid():N}@acme.test";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, admin.Subject, $"{admin.Subject}@acme.test", [TenantRoles.TenantAdmin], "active", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, $"user-{Guid.NewGuid():N}", invitedEmail, [TenantRoles.ContractsOfficer], "invited", Ct);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/staff").As(admin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(Ct);
        var row = html[html.IndexOf($"data-member=\"{invitedEmail}\"", StringComparison.Ordinal)..];
        row = row[..row.IndexOf("</tr>", StringComparison.Ordinal)];
        row.ShouldContain("data-status=\"invited\"");
        row.ShouldContain("Contracts officer");
        row.ShouldContain("data-resend");
        html.ShouldContain("Invite staff member");
    }
}
