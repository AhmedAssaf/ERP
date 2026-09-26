using System.Net;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Web;

/// <summary>W-07 acceptance and the culture order: cookie, locale claim, tenant default, ar-SA.</summary>
[Collection(DatabaseCollection.Name)]
public class LocalizationTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Arabic_profile_renders_an_arabic_right_to_left_page()
    {
        var html = await GetHomeAsync("acme.localhost", TestUser.AcmeAdmin);

        html.ShouldContain("<html lang=\"ar\" dir=\"rtl\">");
        html.ShouldContain("مرحباً بك في Acme Contracting");
        html.ShouldNotContain("Home.Welcome");
    }

    [Fact]
    public async Task English_profile_renders_an_english_left_to_right_page()
    {
        var html = await GetHomeAsync("beta.localhost", TestUser.BetaAdmin);

        html.ShouldContain("<html lang=\"en\" dir=\"ltr\">");
        html.ShouldContain("Welcome to Beta Industries");
    }

    [Fact]
    public async Task The_culture_cookie_overrides_the_profile()
    {
        var html = await GetHomeAsync("acme.localhost", TestUser.AcmeAdmin, cookie: ".AspNetCore.Culture=c%3Den-US%7Cuic%3Den-US");

        html.ShouldContain("<html lang=\"en\" dir=\"ltr\">");
    }

    [Fact]
    public async Task Without_a_profile_locale_the_tenant_default_applies()
    {
        var html = await GetHomeAsync("beta.localhost", TestUser.BetaAdmin with { Locale = null });

        html.ShouldContain("<html lang=\"en\" dir=\"ltr\">");
    }

    [Fact]
    public async Task The_tenant_colour_overrides_the_primary_token()
    {
        var html = await GetHomeAsync("acme.localhost", TestUser.AcmeAdmin);

        html.ShouldContain("--color-primary: #0F766E");
        html.ShouldContain("--color-on-primary: #FFFFFF");
    }

    [Fact]
    public async Task Culture_switch_sets_the_cookie_and_refuses_an_external_return_url()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.GetAsync(new Uri("/culture/set?culture=en-US&returnUrl=https://evil.example/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/");
        response.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith(".AspNetCore.Culture=c%3Den-US", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("%2F%09%2Fevil.example%2F", "/")]
    [InlineData("%2F%0A%2Fevil.example", "/")]
    [InlineData("%2F%5Cevil.example", "/")]
    [InlineData("%2F%2Fevil.example", "/")]
    [InlineData("https%3A%2F%2Fevil.example", "/")]
    [InlineData("%2Ftenders%3Fx%3D1", "/tenders?x=1")]
    public async Task Culture_switch_rejects_control_characters_and_other_open_redirect_attempts(string returnUrl, string expectedLocation)
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.GetAsync(new Uri($"/culture/set?culture=en-US&returnUrl={returnUrl}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe(expectedLocation);
    }

    [Fact]
    public async Task Culture_switch_sets_the_cookie_as_secure_even_over_plain_http()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.GetAsync(new Uri("/culture/set?culture=en-US&returnUrl=%2F", UriKind.Relative), Ct);

        response.Headers.GetValues("Set-Cookie")
            .ShouldContain(c => c.StartsWith(".AspNetCore.Culture=", StringComparison.Ordinal) && c.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> GetHomeAsync(string host, TestUser user, string? cookie = null)
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/").As(user);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Razor may encode non-ASCII characters as entities; decode so assertions read as the user sees the page.
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }
}
