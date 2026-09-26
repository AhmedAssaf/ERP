using Microsoft.Extensions.Configuration;

namespace Platform.Migrator;

// An explicit entry-point class, not top-level statements: top-level statements synthesize a type named
// "Program" in the global namespace, which collides (CS0433) with Platform.Web's Program once both assemblies
// are referenced together by Platform.IntegrationTests (WebApplicationFactory<Program> needs Platform.Web's).
internal static class EntryPoint
{
    private static async Task<int> Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(MigrationRunner).Assembly, optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var owner = configuration.GetConnectionString("Owner");
        if (string.IsNullOrWhiteSpace(owner))
        {
            Console.Error.WriteLine("Connection string 'Owner' is not configured (user secrets or ConnectionStrings__Owner).");
            return 1;
        }

        var applied = await MigrationRunner.RunAsync(owner);
        Console.WriteLine(applied.Count == 0 ? "Database is up to date." : $"Applied {applied.Count} scripts: {string.Join(", ", applied)}");
        return 0;
    }
}
