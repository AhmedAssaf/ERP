using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;
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
}
