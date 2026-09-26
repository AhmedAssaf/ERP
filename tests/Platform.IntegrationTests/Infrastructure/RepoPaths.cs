namespace Platform.IntegrationTests.Infrastructure;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string PostgresInitScript => Path.Combine(Root, "infra", "compose", "postgres", "init", "01-databases.sql");

    public static string KeycloakRealm => Path.Combine(Root, "infra", "compose", "keycloak", "import", "waslabid-realm.json");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WaslaBid.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("WaslaBid.slnx was not found above the test output folder.");
    }
}
