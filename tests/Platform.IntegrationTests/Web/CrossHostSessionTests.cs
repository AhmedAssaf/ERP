using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.IntegrationTests.Infrastructure;
using Platform.Web.PlatformHost;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// QA pass, D-1 and spec 3.1 on the real pages (the existing Keycloak tests prove it on test endpoints): a tenant
/// session presented on the platform host, under either cookie name, opens no console page and is sent to the platform
/// realm; a platform admin's session presented on a tenant host opens no admin page, uploads nothing, and is sent to the
/// tenant realm. Real cookie and OpenID Connect handlers; each realm's configuration is fixed in the test, so the
/// challenge is observable without any network.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CrossHostSessionTests(DatabaseFixture db)
{
    private const string TenantCookie = "waslabid.auth";
    private const string PlatformCookie = "waslabid.platform";
    private const string TenantRealmAuthorize = "https://tenant-realm.invalid/auth";
    private const string PlatformRealmAuthorize = "https://platform-realm.invalid/auth";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> ConsolePagesAndCookieNames()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[] { "/platform", "/platform/tenants", "/platform/usage", "/platform/jobs" })
        {
            data.Add(path, TenantCookie);
            data.Add(path, PlatformCookie);
        }

        return data;
    }

    public static TheoryData<string, string> AdminPagesAndCookieNames()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[] { AdminRequests.StaffPath, AdminRequests.BrandingPath })
        {
            data.Add(path, TenantCookie);
            data.Add(path, PlatformCookie);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ConsolePagesAndCookieNames))]
    public async Task A_tenant_admin_session_on_the_platform_host_is_sent_to_the_platform_realm(string path, string cookieName)
    {
        await using var factory = Factory();
        var session = AuthCookies.Protect(factory.Services, CookieAuthenticationDefaults.AuthenticationScheme, TenantAdminClaims());
        using var client = Client(factory, PlatformWebFactory.PlatformHost);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).WithCookie(cookieName, session), Ct);

        ShouldBeChallengedBy(response, PlatformRealmAuthorize);
    }

    [Theory]
    [MemberData(nameof(AdminPagesAndCookieNames))]
    public async Task A_platform_admin_session_on_a_tenant_host_is_sent_to_the_tenant_realm(string path, string cookieName)
    {
        await using var factory = Factory();
        var session = AuthCookies.Protect(factory.Services, PlatformAuthentication.CookieScheme, PlatformAdminClaims());
        using var client = Client(factory, "acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).WithCookie(cookieName, session), Ct);

        ShouldBeChallengedBy(response, TenantRealmAuthorize);
    }

    [Fact]
    public async Task A_platform_admin_session_on_a_tenant_host_cannot_upload_a_logo()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var factory = Factory();
        var session = AuthCookies.Protect(factory.Services, PlatformAuthentication.CookieScheme, PlatformAdminClaims(tenant.KeycloakOrgAlias));
        using var client = Client(factory, TenantRows.Host(tenant));
        using var form = new MultipartFormDataContent { { new ByteArrayContent(AdminRequests.Png()), "logo", "logo.png" } };

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, AdminRequests.LogoPath) { Content = form }.WithCookie(PlatformCookie, session), Ct);

        ShouldBeChallengedBy(response, TenantRealmAuthorize);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData(TenantCookie)]
    [InlineData(PlatformCookie)]
    public async Task A_vendor_session_on_the_platform_host_cannot_open_the_usage_page(string cookieName)
    {
        // W-10 (spec 6.6): a vendor of acme presents its tenant session to the console's usage page and sees no count.
        await using var factory = Factory();
        var vendor = AuthCookies.Principal(
        [
            new Claim("sub", "acme.vendor"),
            new Claim("preferred_username", "acme.vendor"),
            new Claim("organization", "acme"),
            new Claim("roles", "vendor"),
        ]);
        var session = AuthCookies.Protect(factory.Services, CookieAuthenticationDefaults.AuthenticationScheme, vendor);
        using var client = Client(factory, PlatformWebFactory.PlatformHost);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/platform/usage").WithCookie(cookieName, session), Ct);

        ShouldBeChallengedBy(response, PlatformRealmAuthorize);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("data-tile");
    }

    [Fact]
    public async Task Control_the_platform_session_opens_the_console_on_its_own_host()
    {
        await using var factory = Factory();
        var session = AuthCookies.Protect(factory.Services, PlatformAuthentication.CookieScheme, PlatformAdminClaims());
        using var client = Client(factory, PlatformWebFactory.PlatformHost);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/platform").WithCookie(PlatformCookie, session), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private WebApplicationFactory<Program> Factory() =>
        new PlatformWebFactory(
                db.AppConnectionString,
                new OidcSettings("https://tenant-realm.invalid/realms/waslabid", "unused-in-tests", "https://platform-realm.invalid/realms/waslabid-platform", "unused-in-tests"))
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o => FixRealm(o, TenantRealmAuthorize));
                services.PostConfigure<OpenIdConnectOptions>(PlatformAuthentication.OidcScheme, o => FixRealm(o, PlatformRealmAuthorize));
            }));

    /// <summary>The realm's configuration as a fixed document, replacing the metadata the handler would fetch.</summary>
    private static void FixRealm(OpenIdConnectOptions options, string authorizeEndpoint)
    {
        options.Configuration = new OpenIdConnectConfiguration { AuthorizationEndpoint = authorizeEndpoint };
        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string host) =>
        factory.CreateClient(new() { BaseAddress = new Uri($"https://{host}"), AllowAutoRedirect = false, HandleCookies = false });

    private static ClaimsPrincipal TenantAdminClaims() => AuthCookies.Principal(
    [
        new Claim("sub", "acme.admin"),
        new Claim("preferred_username", "acme.admin"),
        new Claim("organization", "acme"),
        new Claim("acr", "2"),
        new Claim("roles", "platform-admin"),
    ]);

    private static ClaimsPrincipal PlatformAdminClaims(string organization = "acme") => AuthCookies.Principal(
    [
        new Claim("sub", "platform.admin"),
        new Claim("preferred_username", "platform.admin"),
        new Claim("acr", "2"),
        new Claim("roles", "platform-admin"),
        new Claim("organization", organization),
    ]);

    private static void ShouldBeChallengedBy(HttpResponseMessage response, string authorizeEndpoint)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location.ShouldNotBeNull().GetLeftPart(UriPartial.Path).ShouldBe(authorizeEndpoint);
    }
}
