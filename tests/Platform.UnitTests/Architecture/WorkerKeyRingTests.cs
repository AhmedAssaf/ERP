namespace Platform.UnitTests.Architecture;

/// <summary>
/// W-24: the Data Protection key ring belongs to Platform.Web alone. The worker issues and reads no cookie and protects no
/// payload, so neither it nor any project it is built from registers Data Protection or the key ring, and it does not
/// reference the web host (where the ring and its <c>ConnectionStrings:KeyRing</c> credential live). A source check: the
/// worker's entry point is private and no test project references the worker.
/// </summary>
public sealed class WorkerKeyRingTests
{
    private static readonly string[] WorkerFolders = ["Platform.Worker", "Platform.Shared", "Modules"];

    private static readonly string[] KeyRingRegistrations = ["AddDataProtection", "AddKeyRing", "PersistKeysTo", "ConnectionStrings:KeyRing", "GetConnectionString(\"KeyRing\")"];

    [Fact]
    public void The_worker_and_the_projects_it_is_built_from_do_not_load_the_key_ring()
    {
        var folders = WorkerFolders.Select(f => Path.Combine(TestRepo.Src, f)).ToList();
        folders.ShouldAllBe(f => Directory.Exists(f));

        var offenders = folders
            .SelectMany(f => Directory.EnumerateFiles(f, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => KeyRingRegistrations.Any(r => File.ReadAllText(f).Contains(r, StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(TestRepo.Root, f))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void The_worker_does_not_reference_the_web_host()
    {
        var project = File.ReadAllText(Path.Combine(TestRepo.Src, "Platform.Worker", "Platform.Worker.csproj"));

        project.ShouldNotContain("Platform.Web");
    }
}
