using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Web;

/// <summary>W-06 (admin subset): the component gallery exists only in Development, in both cultures and directions.</summary>
[Collection(DatabaseCollection.Name)]
public class GalleryTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Testing", true)]
    [InlineData("Testing", false)]
    [InlineData("Production", true)]
    [InlineData("Production", false)]
    public async Task The_gallery_is_not_served_outside_Development(string environment, bool signedIn)
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString, environment: environment);
        using var client = factory.ClientFor("acme.localhost");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/dev/gallery");
        if (signedIn)
        {
            request.As(TestUser.AcmeAdmin);
        }

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task In_Development_the_gallery_needs_sign_in_like_every_tenant_page()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString, environment: "Development");
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.GetAsync(new Uri("/dev/gallery", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task In_Development_the_gallery_renders_every_component_in_both_cultures_side_by_side()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString, environment: "Development");
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/dev/gallery").As(TestUser.AcmeAdmin), Ct);

        // Development loads user secrets; the test database must still win. Compared without printing either value (N-10).
        var used = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Platform");
        (used == db.AppConnectionString).ShouldBeTrue("the test connection string was overridden by another configuration source");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
        html.ShouldContain("<section lang=\"ar\" dir=\"rtl\"");
        html.ShouldContain("<section lang=\"en\" dir=\"ltr\"");
        foreach (var component in new[] { "AppShell", "Button", "TextField", "Select", "ColorField", "DataTable", "StatusBadge", "Dialog", "Toast", "EmptyState", "AuditList" })
        {
            html.ShouldContain($"data-gallery=\"{component}\"", Case.Sensitive, $"{component} is missing from the gallery");
        }

        // The same component text appears once per culture.
        html.ShouldContain("Sign out");
        html.ShouldContain("تسجيل الخروج");
        html.ShouldContain("Compact rows");
        html.ShouldContain("صفوف مضغوطة");
        // A missing translation renders its key; none may reach the page.
        SharedResourceKeys().Where(key => html.Contains(key, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    private static List<string> SharedResourceKeys()
    {
        var resx = Path.Combine(RepoPaths.Root, "src", "UI", "Platform.UI", "Resources", "SharedResource.en-US.resx");
        return [.. XDocument.Load(resx).Root!.Elements("data").Select(d => (string)d.Attribute("name")!)];
    }
}
