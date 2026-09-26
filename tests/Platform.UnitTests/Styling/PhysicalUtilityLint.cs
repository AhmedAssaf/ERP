using System.Text.RegularExpressions;

namespace Platform.UnitTests.Styling;

/// <summary>
/// Finds physical direction utilities in markup, C# and CSS under a folder. A line containing
/// "lint-physical: allow" is skipped (for a rare legitimate case, with the reason on the same line).
/// </summary>
internal static partial class PhysicalUtilityLint
{
    private static readonly string[] Extensions = [".razor", ".cshtml", ".cs", ".css"];
    private static readonly string[] SkippedFolders = ["bin", "obj", "wwwroot", "node_modules"];

    public static IEnumerable<string> Scan(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)
                .Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("lint-physical: allow", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var utility in FindIn(lines[i]))
                {
                    yield return $"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}: {utility}";
                }
            }
        }
    }

    public static IReadOnlyList<string> FindIn(string text) =>
        PhysicalUtility().Matches(text).Select(m => m.Value).ToList();

    [GeneratedRegex(
        @"(?<![\w-])(?:(?:scroll-)?(?:ml|mr|pl|pr)-[\w\[\]./%-]+|(?:left|right)-[\w\[\]./%-]+|(?:border|rounded)-(?:l|r|tl|tr|bl|br)(?:-[\w\[\]./%-]+)?|text-(?:left|right)|float-(?:left|right)|clear-(?:left|right))(?![\w-])")]
    private static partial Regex PhysicalUtility();
}
