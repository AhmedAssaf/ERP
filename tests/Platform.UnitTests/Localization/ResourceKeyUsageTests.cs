using System.Text.RegularExpressions;
using System.Xml.Linq;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors.Contracts;

namespace Platform.UnitTests.Localization;

/// <summary>
/// QA pass, W-07 and docs/07 principle 5 across the whole solution: every resource file under <c>src</c> has its other
/// language beside it with the same keys and no empty value, and every key the pages ask the shared resource for, literal
/// or built from a role, health status, component or tenant status, exists in both languages. A missing key renders as
/// the key itself, so this catches the raw key before any page shows it.
/// </summary>
public partial class ResourceKeyUsageTests
{
    // The statuses vendor.cr_disputes stores (W-33, vendors migration 0018), shown on /vendor/dispute.
    private static readonly string[] DisputeStatuses = ["open", "under_review", "upheld", "rejected"];

    private static readonly string SharedFolder = Path.Combine(TestRepo.Src, "UI", "Platform.UI", "Resources");

    public static TheoryData<string> ResourceBases()
    {
        var bases = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in ResxFiles())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var dot = name.LastIndexOf('.');
            bases.Add(Path.Combine(Path.GetDirectoryName(file)!, dot < 0 ? name : name[..dot]));
        }

        return new TheoryData<string>(bases);
    }

    [Fact]
    public void Resource_files_exist_in_src()
    {
        ResxFiles().Count.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Theory]
    [MemberData(nameof(ResourceBases))]
    public void Every_resource_file_has_arabic_and_english_with_the_same_keys_and_no_empty_value(string basePath)
    {
        var arabicFile = basePath + ".ar-SA.resx";
        var englishFile = basePath + ".en-US.resx";
        File.Exists(arabicFile).ShouldBeTrue($"{arabicFile} is missing");
        File.Exists(englishFile).ShouldBeTrue($"{englishFile} is missing");
        var arabic = Read(arabicFile);
        var english = Read(englishFile);

        arabic.Keys.Order(StringComparer.Ordinal).ShouldBe(english.Keys.Order(StringComparer.Ordinal));
        arabic.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ShouldBeEmpty("empty Arabic values");
        english.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ShouldBeEmpty("empty English values");
    }

    [Fact]
    public void Every_resource_file_is_named_for_ar_SA_or_en_US()
    {
        ResxFiles()
            .Select(Path.GetFileName)
            .Where(f => !f!.EndsWith(".ar-SA.resx", StringComparison.Ordinal) && !f.EndsWith(".en-US.resx", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Every_literal_shared_resource_key_used_in_src_exists_in_both_languages()
    {
        var arabic = Read(Path.Combine(SharedFolder, "SharedResource.ar-SA.resx"));
        var english = Read(Path.Combine(SharedFolder, "SharedResource.en-US.resx"));
        var used = SourceFiles()
            .SelectMany(f => LiteralKey().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: m.Groups["key"].Value)))
            .ToList();

        used.ShouldNotBeEmpty();
        used.Where(u => !english.ContainsKey(u.Key) || !arabic.ContainsKey(u.Key))
            .Select(u => $"{u.File}: {u.Key}")
            .Distinct()
            .ShouldBeEmpty();
    }

    [Fact]
    public void Every_key_built_from_a_value_exists_in_both_languages()
    {
        var arabic = Read(Path.Combine(SharedFolder, "SharedResource.ar-SA.resx"));
        var english = Read(Path.Combine(SharedFolder, "SharedResource.en-US.resx"));
        var built = TenantRoles.All.Select(r => $"Staff.Role.{r}")
            .Concat(Enum.GetNames<HealthStatus>().Append("Unknown").Select(s => $"Console.Status.{s}"))
            .Concat(HealthComponents.Board.Append(HealthComponents.Disk).Select(c => $"Console.Component.{c}"))
            .Append("Console.TenantStatus.active")
            .Concat(Enum.GetNames<CrLookupOutcome>().Select(o => $"Admin.Vendors.Ownership.Lookup.{o}"))
            .Concat(Enum.GetNames<OwnershipVerificationMethod>().Select(m => $"Admin.Vendors.Ownership.Method.{m}"))
            .Concat(Enum.GetNames<CrOwnershipMethod>().Select(m => $"Console.Ownership.Method.{m}"))
            .Concat(Enum.GetNames<OwnershipVerificationMethod>().Select(m => $"Console.Ownership.Verified.{m}"))
            .Concat(DisputeStatuses.Select(s => $"Vendor.Dispute.Status.{s}"))
            .ToList();

        built.Where(k => !english.ContainsKey(k) || !arabic.ContainsKey(k)).ShouldBeEmpty();
    }

    [Fact]
    public void Every_key_built_by_interpolation_is_one_of_the_families_checked_here()
    {
        // A new T[$"..."] family must be added to the test above, or its keys go unchecked.
        var families = SourceFiles()
            .SelectMany(f => InterpolatedKey().Matches(File.ReadAllText(f)).Select(m => m.Groups["prefix"].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        families.ShouldBeSubsetOf(
        [
            "Admin.Vendors.Ownership.Lookup.", "Admin.Vendors.Ownership.Method.", "Console.Component.", "Console.Ownership.Method.",
            "Console.Ownership.Verified.", "Console.Status.", "Console.TenantStatus.", "Staff.Role.", "Vendor.Dispute.Status.",
        ]);
    }

    private static List<string> ResxFiles() =>
        [.. Directory.EnumerateFiles(TestRepo.Src, "*.resx", SearchOption.AllDirectories).Where(NotBuildOutput)];

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(TestRepo.Src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(NotBuildOutput);

    private static bool NotBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !parts.Contains("obj") && !parts.Contains("bin");
    }

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(file).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    // T["Key"] or T["Key", args]: the shared localizer is injected as T everywhere in src.
    [GeneratedRegex("""\bT\["(?<key>[A-Za-z0-9_.\-]+)"[,\]]""")]
    private static partial Regex LiteralKey();

    [GeneratedRegex("""\bT\[\$"(?<prefix>[A-Za-z0-9_.\-]*)\{""")]
    private static partial Regex InterpolatedKey();
}
