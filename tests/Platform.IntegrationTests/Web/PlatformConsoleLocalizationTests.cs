using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Platform.IntegrationTests.Infrastructure;
using Platform.Web.PlatformHost;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// QA pass, W-07 on the platform console (F-51, F-54): each console page renders right to left in Arabic and left to
/// right in English by the culture cookie, with no resource key on the page in either language.
/// </summary>
public sealed partial class PlatformConsoleTests
{
    [Theory]
    [InlineData("/platform", "ar-SA", "<html lang=\"ar\" dir=\"rtl\">")]
    [InlineData("/platform/tenants", "ar-SA", "<html lang=\"ar\" dir=\"rtl\">")]
    [InlineData("/platform", "en-US", "<html lang=\"en\" dir=\"ltr\">")]
    [InlineData("/platform/tenants", "en-US", "<html lang=\"en\" dir=\"ltr\">")]
    public async Task A_console_page_follows_the_culture_and_shows_no_resource_key(string path, string culture, string htmlTag)
    {
        await using var factory = Factory();

        var html = await ConsolePageInAsync(factory, path, culture);

        html.ShouldContain(htmlTag);
        ConsoleResourceKeys().Where(key => html.Contains(key, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    private static async Task<string> ConsolePageInAsync(WebApplicationFactory<Program> factory, string path, string culture)
    {
        var cookie = AuthCookies.Protect(factory.Services, PlatformAuthentication.CookieScheme, PlatformAdmin());
        using var client = factory.CreateClient(new() { BaseAddress = new Uri($"http://{PlatformWebFactory.PlatformHost}"), AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var value = Uri.EscapeDataString($"c={culture}|uic={culture}");
        request.Headers.Add("Cookie", $"{PlatformCookie}={cookie}; .AspNetCore.Culture={value}");
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    private static List<string> ConsoleResourceKeys()
    {
        var resx = Path.Combine(RepoPaths.Root, "src", "UI", "Platform.UI", "Resources", "SharedResource.en-US.resx");
        return [.. XDocument.Load(resx).Root!.Elements("data").Select(d => (string)d.Attribute("name")!)];
    }
}
