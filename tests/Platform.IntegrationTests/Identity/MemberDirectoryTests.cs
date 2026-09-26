using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Results;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// F-07 (spec 4.1): members and roles per tenant in identity.members. Each test works in a tenant of its own, so rows
/// from other tests never count.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MemberDirectoryTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_last_tenant_admin_cannot_drop_their_admin_role()
    {
        var tenant = MemberRows.NewTenant();
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "admin-a", "a@t.test", [TenantRoles.TenantAdmin], "active", Ct);
        // An invited admin has not signed in yet, so the tenant would still have nobody able to administer it.
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "admin-c", "c@t.test", [TenantRoles.TenantAdmin], "invited", Ct);
        await using var host = new ModuleHost(db.AppConnectionString);

        await using (var scope = host.ScopeFor(tenant))
        {
            var members = scope.ServiceProvider.GetRequiredService<IMemberDirectory>();
            var refused = await members.SetRolesAsync("admin-a", [TenantRoles.ContractsOfficer], "admin-a", Ct);

            refused.IsSuccess.ShouldBeFalse();
            refused.Error.Kind.ShouldBe(ErrorKind.Refused);
            refused.Error.Code.ShouldBe("identity.last_tenant_admin");
            (await members.GetRolesAsync("admin-a", Ct)).ShouldBe([TenantRoles.TenantAdmin]);
        }

        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, tenant.TenantId, "admin-a", "identity.last_admin_protected", Ct)).ShouldBe(1);

        // With a second active admin the change goes through and is audited.
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "admin-b", "b@t.test", [TenantRoles.TenantAdmin], "active", Ct);
        await using (var scope = host.ScopeFor(tenant))
        {
            var members = scope.ServiceProvider.GetRequiredService<IMemberDirectory>();
            var changed = await members.SetRolesAsync("admin-a", [TenantRoles.ContractsOfficer], "admin-a", Ct);

            changed.IsSuccess.ShouldBeTrue();
            changed.Value.Roles.ShouldBe([TenantRoles.ContractsOfficer]);
            (await members.GetRolesAsync("admin-a", Ct)).ShouldBe([TenantRoles.ContractsOfficer]);
        }

        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, tenant.TenantId, "admin-a", "identity.roles_changed", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_roles_and_unknown_members_are_refused_without_a_change()
    {
        var tenant = MemberRows.NewTenant();
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "admin", "admin@t.test", [TenantRoles.TenantAdmin], "active", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "officer", "officer@t.test", [TenantRoles.ContractsOfficer], "active", Ct);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(tenant);
        var members = scope.ServiceProvider.GetRequiredService<IMemberDirectory>();

        var unknownRole = await members.SetRolesAsync("officer", ["auditor"], "admin", Ct);
        var unknownMember = await members.SetRolesAsync("nobody", [TenantRoles.ContractsOfficer], "admin", Ct);

        unknownRole.Error!.Kind.ShouldBe(ErrorKind.Validation);
        unknownMember.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await members.GetRolesAsync("officer", Ct)).ShouldBe([TenantRoles.ContractsOfficer]);
        (await members.GetRolesAsync("nobody", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tenant_lists_only_its_own_members()
    {
        var first = MemberRows.NewTenant();
        var second = MemberRows.NewTenant();
        await MemberRows.InsertAsync(db.AppConnectionString, first.TenantId, "u1", "zed@first.test", [TenantRoles.TenantAdmin], "active", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, first.TenantId, null, "amal@first.test", [TenantRoles.FinanceApprover], "invited", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, second.TenantId, "u2", "other@second.test", [TenantRoles.TenantAdmin], "active", Ct);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(first);

        var listed = await scope.ServiceProvider.GetRequiredService<IMemberDirectory>().ListAsync(Ct);

        listed.Select(m => m.Email).ShouldBe(["amal@first.test", "zed@first.test"]);
        listed[0].UserId.ShouldBeNull();
        listed[0].Status.ShouldBe(MemberStatus.Invited);
        listed[1].Status.ShouldBe(MemberStatus.Active);
        listed[1].ActivatedAt.ShouldNotBeNull();
    }
}
