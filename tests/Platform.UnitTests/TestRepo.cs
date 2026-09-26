namespace Platform.UnitTests;

internal static class TestRepo
{
    public static string Root { get; } = FindRoot();

    public static string Src => Path.Combine(Root, "src");

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
