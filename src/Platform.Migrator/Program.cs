using Microsoft.Extensions.Configuration;

namespace Platform.Migrator;

// An explicit entry-point class, not top-level statements: top-level statements synthesize a type named
// "Program" in the global namespace, which collides (CS0433) with Platform.Web's Program once both assemblies
// are referenced together by Platform.IntegrationTests (WebApplicationFactory<Program> needs Platform.Web's).
internal static class EntryPoint
{
    private static async Task<int> Main(string[] args)
    {
        var seedDev = args.Contains("--seed-dev", StringComparer.Ordinal);
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(MigrationRunner).Assembly, optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args.Where(a => a != "--seed-dev").ToArray())
            .Build();

        var owner = configuration.GetConnectionString("Owner");
        if (string.IsNullOrWhiteSpace(owner))
        {
            Console.Error.WriteLine("Connection string 'Owner' is not configured (user secrets or ConnectionStrings__Owner).");
            return 1;
        }

        var applied = await MigrationRunner.RunAsync(owner);
        Console.WriteLine(applied.Count == 0 ? "Database is up to date." : $"Applied {applied.Count} scripts: {string.Join(", ", applied)}");

        if (seedDev)
        {
            var app = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(app))
            {
                Console.Error.WriteLine("--seed-dev needs connection string 'Platform' (the erp_app role).");
                return 1;
            }

            await DevSeed.SeedTenantsAsync(owner);
            await DevSeed.SeedWorkflowsAsync(app);
            await DevSeed.SeedMembersAsync(app);
            await DevSeed.SeedConsentRecipientsAsync(owner);
            Console.WriteLine("Seeded development tenants acme and beta with the default approval chain and a tenant admin each, and a test consent recipient.");
        }

        return 0;
    }
}
