using System.Xml.Linq;

namespace Platform.UnitTests.Localization;

/// <summary>docs/07 principle 5: Arabic and English ship together; no key exists in one language only.</summary>
public class ResourceParityTests
{
    private static readonly string Resources = Path.Combine(TestRepo.Src, "UI", "Platform.UI", "Resources");

    [Fact]
    public void Arabic_and_English_resources_have_the_same_keys_and_no_empty_values()
    {
        var arabic = Read("SharedResource.ar-SA.resx");
        var english = Read("SharedResource.en-US.resx");

        arabic.Keys.Except(english.Keys).ShouldBeEmpty("keys only in Arabic");
        english.Keys.Except(arabic.Keys).ShouldBeEmpty("keys only in English");
        arabic.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ShouldBeEmpty("empty Arabic values");
        english.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ShouldBeEmpty("empty English values");
    }

    [Fact]
    public void The_invitation_notice_has_the_same_keys_in_both_languages_and_every_role()
    {
        var folder = Path.Combine(TestRepo.Src, "Modules", "Identity", "Platform.Modules.Identity", "Resources", "Members");
        var arabic = Read(Path.Combine(folder, "InvitationNotice.ar-SA.resx"));
        var english = Read(Path.Combine(folder, "InvitationNotice.en-US.resx"));

        arabic.Keys.Order(StringComparer.Ordinal).ShouldBe(english.Keys.Order(StringComparer.Ordinal));
        arabic.Values.Concat(english.Values).ShouldAllBe(v => !string.IsNullOrWhiteSpace(v));
        foreach (var role in Platform.Modules.Identity.Contracts.TenantRoles.All)
        {
            english.ShouldContainKey($"Role.{role}");
        }
    }

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(Path.Combine(Resources, file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);
}
