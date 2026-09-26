namespace Platform.UITests.Conventions;

/// <summary>docs/08 sections 6 and 9: sentence case everywhere, so no component forces upper case.</summary>
public class ComponentConventionTests
{
    [Fact]
    public void No_shared_component_uses_uppercase_text()
    {
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "UI", "Platform.UI"), "*.razor", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("uppercase", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f));

        offenders.ShouldBeEmpty();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WaslaBid.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("WaslaBid.slnx was not found above the test output folder.");
    }
}
