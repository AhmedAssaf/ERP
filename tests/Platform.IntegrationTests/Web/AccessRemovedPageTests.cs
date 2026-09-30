using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.IntegrationTests.Infrastructure;
using Platform.Shared.Tenancy;
using Platform.UI;
using Platform.Web.Account;
using Platform.Web.Components.Pages.Account;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-21, QA D2 (decided by the user 2026-09-29): a signed-in user on a tenant host who is not in that tenant's
/// organization (a removed staff member signed straight back in by Keycloak's session, a vendor of another tenant) gets
/// a page that says their access to the tenant was removed, in Arabic and English, with a Sign out button that ends the
/// local cookie and the Keycloak session (RP-initiated logout with the id token as hint), instead of an empty 403. It is
/// the body of that same 403, never a redirect or a challenge (a browser must not be sent into another tenant, pentest
/// of the vendor slice); a refusal for a missing role, a refused Blazor connection and a refused post stay plain 403s.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class AccessRemovedPageTests(DatabaseFixture db) : IDisposable
{
    private const string TenantCookie = "waslabid.auth";
    private const string EndSession = "https://tenant-realm.invalid/logout";

    private readonly BunitContext _page = new();

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ar-SA", "أُلغي وصولك إلى أكمي. تواصل مع مسؤول النظام لديك.", "تسجيل الخروج")]
    [InlineData("en-US", "Your access to Acme was removed. Contact your administrator.", "Sign out")]
    public void The_page_names_the_tenant_and_offers_sign_out_in_both_languages(string culture, string message, string signOut)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        var tenants = new TenantAccessor();
        tenants.Set(TestTenants.Acme with { Branding = TestTenants.Acme.Branding with { PortalName = culture == "ar-SA" ? "أكمي" : "Acme" } });
        _page.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        _page.Services.AddPlatformUI();
        _page.Services.AddSingleton<ITenantAccessor>(tenants);
        _page.Services.AddSingleton<AntiforgeryStateProvider, FixedAntiforgery>();

        var page = _page.Render<AccessRemoved>();

        page.Find("[data-access-removed]").TextContent.ShouldContain(message);
        var form = page.Find("form[data-access-removed-sign-out]");
        form.GetAttribute("method").ShouldBe("post");
        form.GetAttribute("action").ShouldBe("/account/sign-out");
        form.QuerySelector("input[name=__RequestVerificationToken]").ShouldNotBeNull();
        form.QuerySelector("button[type=submit]")!.TextContent.Trim().ShouldBe(signOut);
        RawKey().IsMatch(page.Markup).ShouldBeFalse("no resource key renders as itself");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/admin/staff")]
    [InlineData("/admin/branding")]
    public async Task A_signed_in_user_without_the_host_organization_gets_the_access_removed_page_as_a_403(string path)
    {
        await using var factory = Factory();
        using var client = Client(factory);

        using var response = await client.SendAsync(Get(path, Session(factory, Principal("w21.outsider", organization: null))), Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the page is the body of the 403, never a redirect or a challenge");
        html.ShouldContain("data-access-removed");
        // English by the principal's locale claim; the portal name is whatever acme's branding says in the shared database.
        html.ShouldMatch(@"Your access to [^<]+ was removed\. Contact your administrator\.");
        html.ShouldContain("action=\"/account/sign-out\"");
    }

    [Fact]
    public async Task A_session_of_another_tenant_replayed_on_this_host_gets_the_page_and_no_challenge()
    {
        await using var factory = Factory();
        using var client = Client(factory);

        using var response = await client.SendAsync(Get("/admin/staff", Session(factory, Principal("w21.beta.user", organization: "beta"))), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.Location.ShouldBeNull();
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("data-access-removed");
    }

    [Fact]
    public async Task A_member_refused_for_a_missing_role_still_gets_a_plain_403()
    {
        // In the organization, no member row: the tenant-admin page refuses it for the role, not the membership.
        await using var factory = Factory();
        using var client = Client(factory);

        using var response = await client.SendAsync(Get("/admin/staff", Session(factory, Principal($"w21.member.{Guid.NewGuid():N}", organization: "acme"))), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("data-access-removed");
    }

    [Fact]
    public async Task A_refused_blazor_connection_or_form_post_gets_a_plain_403()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var session = Session(factory, Principal("w21.outsider", organization: null));

        using var negotiate = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1").WithCookie(TenantCookie, session), Ct);
        using var post = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/admin/staff") { Content = new FormUrlEncodedContent([]) }.WithCookie(TenantCookie, session), Ct);

        negotiate.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await negotiate.Content.ReadAsStringAsync(Ct)).ShouldNotContain("data-access-removed");
        post.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        (await post.Content.ReadAsStringAsync(Ct)).ShouldNotContain("data-access-removed");
    }

    [Fact]
    public async Task The_page_opened_directly_by_a_member_goes_home()
    {
        await using var factory = Factory();
        using var client = Client(factory);

        using var response = await client.SendAsync(Get(AccessRemovedPage.Path, Session(factory, Principal("w21.member", organization: "acme"))), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsoluteUri.ShouldBe("https://acme.localhost/", "home on the same host, nowhere else");
    }

    [Fact]
    public async Task Signing_out_from_the_page_posts_the_id_token_hint_to_keycloak_in_a_form_not_a_url()
    {
        await using var factory = Factory();
        using var client = Client(factory);
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30) };
        MembershipRevalidation.StampSignIn(properties, DateTimeOffset.UtcNow);
        properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = "header.payload.signature" }]);
        var session = AuthCookies.Protect(factory.Services, CookieAuthenticationDefaults.AuthenticationScheme, Principal("w21.outsider", organization: null), properties);
        using var page = await client.SendAsync(Get("/admin/staff", session), Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);
        var token = AntiforgeryField().Match(html).Groups["value"].Value;
        var antiforgery = page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/sign-out")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token) }),
        };
        request.Headers.Add("Cookie", $"{TenantCookie}={session}; {antiforgery}");
        using var signOut = await client.SendAsync(request, Ct);

        // Final check nit: the id token is personal data, so it goes to Keycloak in a form post, never in a URL that browser
        // history and access logs keep. The form carries the protected state, so the return path still works.
        signOut.StatusCode.ShouldBe(HttpStatusCode.OK);
        signOut.Headers.Location.ShouldBeNull();
        var form = await signOut.Content.ReadAsStringAsync(Ct);
        form.ShouldMatch($"(?i)<form[^>]*method=\"post\"[^>]*action=\"{Regex.Escape(EndSession)}\"");
        form.ShouldContain("name=\"id_token_hint\" value=\"header.payload.signature\"");
        form.ShouldContain("name=\"state\"");
        form.ShouldContain("name=\"post_logout_redirect_uri\"");
        signOut.Headers.GetValues("Cache-Control").ShouldContain(v => v.Contains("no-store", StringComparison.Ordinal));
        signOut.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith($"{TenantCookie}=;", StringComparison.Ordinal), "the local cookie is deleted");
    }

    [Fact]
    public void The_tenant_scheme_keeps_the_id_token_for_the_keycloak_sign_out()
    {
        var properties = new AuthenticationProperties();
        var context = new Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            new ClaimsPrincipal(),
            properties)
        {
            TokenEndpointResponse = new OpenIdConnectMessage { IdToken = "header.payload.signature", AccessToken = "never-stored" },
        };

        SignOutEndpoints.KeepIdToken(context);

        properties.GetTokenValue("id_token").ShouldBe("header.payload.signature");
        properties.GetTokenValue("access_token").ShouldBeNull("only the id token is kept, for the logout hint");
    }

    public void Dispose() => _page.Dispose();

    private WebApplicationFactory<Program> Factory() =>
        new PlatformWebFactory(db.AppConnectionString, new OidcSettings("https://tenant-realm.invalid/realms/waslabid", "unused-in-tests"))
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                {
                    o.Configuration = new OpenIdConnectConfiguration
                    {
                        AuthorizationEndpoint = "https://tenant-realm.invalid/auth",
                        EndSessionEndpoint = EndSession,
                    };
                    o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(o.Configuration);
                })));

    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new() { BaseAddress = new Uri("https://acme.localhost"), AllowAutoRedirect = false, HandleCookies = false });

    private static HttpRequestMessage Get(string path, string session) => new HttpRequestMessage(HttpMethod.Get, path).WithCookie(TenantCookie, session);

    private static string Session(WebApplicationFactory<Program> factory, ClaimsPrincipal user) =>
        AuthCookies.Protect(factory.Services, CookieAuthenticationDefaults.AuthenticationScheme, user);

    private static ClaimsPrincipal Principal(string subject, string? organization)
    {
        var claims = new List<Claim> { new("sub", subject), new("preferred_username", subject), new("locale", "en") };
        if (organization is not null)
        {
            claims.Add(new Claim("organization", organization));
        }

        return AuthCookies.Principal(claims);
    }

    private sealed class FixedAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("test-token", "__RequestVerificationToken");
    }

    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]*)\"")]
    private static partial Regex AntiforgeryField();

    // A shared resource key rendered as itself, as a missing key would render.
    [GeneratedRegex(@"\bAccount\.[A-Z][A-Za-z]+\.[A-Za-z.]+")]
    private static partial Regex RawKey();
}
