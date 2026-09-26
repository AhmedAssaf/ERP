using System.Net;
using System.Xml.Linq;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// QA pass, W-07 acceptance on the admin slice's pages: for a user whose profile is Arabic, <c>/admin/staff</c> and
/// <c>/admin/branding</c> render <c>&lt;html lang="ar" dir="rtl"&gt;</c>, show no resource key, and show none of the
/// English sentences of the shared resource (no fallback to English). The English profile gets the mirror image.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdminLocalizationTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Lazy<(Dictionary<string, string> Arabic, Dictionary<string, string> English)> Resources = new(() =>
    {
        var folder = Path.Combine(RepoPaths.Root, "src", "UI", "Platform.UI", "Resources");
        return (Read(Path.Combine(folder, "SharedResource.ar-SA.resx")), Read(Path.Combine(folder, "SharedResource.en-US.resx")));
    });

    [Theory]
    [MemberData(nameof(AdminRequests.Pages), MemberType = typeof(AdminRequests))]
    public async Task An_arabic_profile_gets_the_admin_page_in_arabic_right_to_left(string path)
    {
        var html = await PageAsync(path, "ar");

        html.ShouldContain("<html lang=\"ar\" dir=\"rtl\">");
    }

    [Theory]
    [MemberData(nameof(AdminRequests.Pages), MemberType = typeof(AdminRequests))]
    public async Task The_arabic_admin_page_shows_no_resource_key(string path)
    {
        var html = await PageAsync(path, "ar");

        Resources.Value.English.Keys.Where(key => html.Contains(key, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AdminRequests.Pages), MemberType = typeof(AdminRequests))]
    public async Task The_arabic_admin_page_falls_back_to_no_english_sentence(string path)
    {
        var html = await PageAsync(path, "ar");

        // Sentences only (two words or more, not the same in both files), so brand names and codes do not count.
        var (arabic, english) = Resources.Value;
        english
            .Where(p => p.Value.Contains(' ', StringComparison.Ordinal) && p.Value.Length >= 12 && arabic.GetValueOrDefault(p.Key) != p.Value)
            .Where(p => !p.Value.Contains('{', StringComparison.Ordinal))
            .Where(p => html.Contains(p.Value, StringComparison.Ordinal))
            .Select(p => p.Key)
            .ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AdminRequests.Pages), MemberType = typeof(AdminRequests))]
    public async Task An_english_profile_gets_the_admin_page_in_english_left_to_right(string path)
    {
        var html = await PageAsync(path, "en");

        html.ShouldContain("<html lang=\"en\" dir=\"ltr\">");
        Resources.Value.English.Keys.Where(key => html.Contains(key, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AdminRequests.Pages), MemberType = typeof(AdminRequests))]
    public async Task The_arabic_culture_cookie_turns_an_english_profile_right_to_left(string path)
    {
        var html = await PageAsync(path, "en", cookie: ".AspNetCore.Culture=c%3Dar-SA%7Cuic%3Dar-SA");

        html.ShouldContain("<html lang=\"ar\" dir=\"rtl\">");
    }

    private async Task<string> PageAsync(string path, string locale, string? cookie = null)
    {
        var (tenant, admin) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], locale, Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.GetAsync(client, path, admin, Ct, cookie);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(file).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);
}
