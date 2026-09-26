using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Workflow.Contracts;

namespace Platform.IntegrationTests.Data;

[Collection(DatabaseCollection.Name)]
public class DevSeedTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeding_twice_leaves_each_dev_tenant_with_one_default_chain()
    {
        await DevSeed.SeedTenantsAsync(db.OwnerConnectionString, Ct);
        await DevSeed.SeedWorkflowsAsync(db.AppConnectionString, Ct);
        await DevSeed.SeedWorkflowsAsync(db.AppConnectionString, Ct);

        await using var host = new ModuleHost(db.AppConnectionString);
        foreach (var tenant in DevSeed.Tenants)
        {
            await using var scope = host.ScopeFor(tenant.ToContext());
            (await scope.ServiceProvider.GetRequiredService<IWorkflowDefinitions>().FindDefaultAsync(Ct)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task Seeding_twice_leaves_each_dev_tenant_with_one_admin_member_keyed_by_email()
    {
        // The seed cannot know the Keycloak sub, so the row carries the realm user's email and no user id; the members
        // claims transformation binds it on the admin's first sign-in with that verified email.
        await DevSeed.SeedMembersAsync(db.AppConnectionString, Ct);
        await DevSeed.SeedMembersAsync(db.AppConnectionString, Ct);

        foreach (var tenant in DevSeed.Tenants)
        {
            var admin = await MemberRows.FindByEmailAsync(db.AppConnectionString, tenant.Id, tenant.AdminEmail, Ct);
            admin.ShouldNotBeNull();
            admin.Roles.ShouldBe([TenantRoles.TenantAdmin]);
            admin.Status.ShouldBe("invited");
            admin.UserId.ShouldBeNull();
        }

        DevSeed.Acme.AdminEmail.ShouldBe("admin@acme.waslabid.test");
        DevSeed.Beta.AdminEmail.ShouldBe("admin@beta.waslabid.test");
    }
}
