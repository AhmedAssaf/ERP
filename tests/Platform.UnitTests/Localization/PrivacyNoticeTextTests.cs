using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Platform.Modules.Vendors.Contracts;

namespace Platform.UnitTests.Localization;

/// <summary>
/// V-14: what each vendor user accepted stays known, so a published privacy notice text never changes in place. The
/// SHA-256 of every published version in both languages is pinned here; editing a published text fails this test, and a
/// new text goes in as a new version (<c>Vendor.Privacy.V2</c>, <see cref="VendorPrivacyNotice.CurrentVersion"/> moved to
/// it, and its hashes added below) while the old text stays.
/// </summary>
public class PrivacyNoticeTextTests
{
    private static readonly string Resources = Path.Combine(TestRepo.Src, "UI", "Platform.UI", "Resources");

    /// <summary>Version, culture and the SHA-256 (lower-case hex) of the UTF-8 text as published.</summary>
    private static readonly (string Version, string Culture, string Sha256)[] Pinned =
    [
        ("V1", "ar-SA", "7469ae6e3ff6c796759b2ff03b2c3bb16524b890f41be63a73c30cb9ea839d9e"),
        ("V1", "en-US", "a9a2db46a2c5998bf0f66c8acc4565d173737c65a4359aa60cf0815539f3886f"),
    ];

    public static TheoryData<string, string, string> Published
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var (version, culture, sha256) in Pinned)
            {
                data.Add(version, culture, sha256);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Published))]
    public void A_published_privacy_notice_text_is_never_changed_in_place(string version, string culture, string sha256)
    {
        var text = Text(culture, version).ShouldNotBeNull($"Vendor.Privacy.{version} is missing from the {culture} resources");

        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ShouldBe(
            sha256, $"Vendor.Privacy.{version} ({culture}) changed; publish the new text as a new version instead");
    }

    [Fact]
    public void The_current_version_is_pinned_in_every_culture_it_is_shown_in()
    {
        var pinned = Pinned.Select(p => (p.Version, p.Culture)).ToHashSet();

        foreach (var culture in VendorPrivacyNotice.Cultures)
        {
            pinned.ShouldContain((VendorPrivacyNotice.CurrentVersion, culture));
        }
    }

    private static string? Text(string culture, string version) =>
        XDocument.Load(Path.Combine(Resources, $"SharedResource.{culture}.resx")).Root!.Elements("data")
            .Where(d => (string?)d.Attribute("name") == $"Vendor.Privacy.{version}")
            .Select(d => (string?)d.Element("value"))
            .SingleOrDefault();
}
