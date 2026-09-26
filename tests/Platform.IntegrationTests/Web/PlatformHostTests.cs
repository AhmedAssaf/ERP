using System.Net;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.IntegrationTests.Infrastructure;
using Platform.Web.PlatformHost;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// The platform console's host (spec 3.1, D-1): only platform paths and the shared framework paths answer on it, and
/// platform paths answer nowhere else. Both refusals are a 404 before authentication, so neither host reveals the
/// other's pages (docs/09 F-51: a tenant admin requesting the console URL gets 404).
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PlatformHostTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/admin")]
    [InlineData("/signin-oidc")]
    [InlineData("/health/ready")]
    [InlineData("/platformx")]
    [InlineData("/account/sign-out")]
    public async Task A_tenant_path_on_the_platform_host_is_404(string path)
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor(PlatformWebFactory.PlatformHost);

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/platform")]
    [InlineData("/platform/tenants")]
    [InlineData("/PLATFORM/Tenants")]
    [InlineData("/signin-platform")]
    [InlineData("/signout-callback-platform")]
    [InlineData("/platform/sign-out")]
    [InlineData(PlatformEndpointStartupFilter.DefaultPolicyPath)]
    [InlineData(PlatformEndpointStartupFilter.FallbackPolicyPath)]
    public async Task A_platform_path_on_a_tenant_host_is_404(string path)
    {
        // The test endpoints exist under /platform, so a 404 here comes from the host rule, not from a missing route.
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        await using var withEndpoints = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, PlatformEndpointStartupFilter>()));
        using var client = withEndpoints.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).As(TestUser.AcmeAdmin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_health_endpoint_answers_on_the_platform_host()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor(PlatformWebFactory.PlatformHost);

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_platform_cookie_is_hardened_like_the_tenant_cookie()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        var cookies = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();

        var platform = cookies.Get(PlatformAuthentication.CookieScheme);

        platform.Cookie.Name.ShouldBe("waslabid.platform");
        platform.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.Always);
        platform.Cookie.HttpOnly.ShouldBeTrue();
        platform.Cookie.SameSite.ShouldBe(SameSiteMode.Lax);
        // Shorter than the tenant cookie's 30 minutes: a stolen console session is worth more (D-2).
        platform.ExpireTimeSpan.ShouldBe(TimeSpan.FromMinutes(15));
        platform.SlidingExpiration.ShouldBeFalse();
        cookies.Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie.Name.ShouldBe("waslabid.auth");
    }

    [Theory]
    [InlineData("1", HttpStatusCode.Forbidden)]
    [InlineData("2", HttpStatusCode.OK)]
    public async Task An_endpoint_asking_only_for_the_role_still_needs_acr_2(string acr, HttpStatusCode expected)
    {
        // Real cookie handlers (the Keycloak address is never contacted: neither answer is a challenge).
        await using var factory = new PlatformWebFactory(
            db.AppConnectionString, new OidcSettings("https://keycloak.invalid/realms/waslabid", "unused-in-tests"));
        await using var withEndpoints = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, PlatformEndpointStartupFilter>()));
        var user = AuthCookies.Principal(
        [
            new Claim("sub", "platform.admin"),
            new Claim("preferred_username", "platform.admin"),
            new Claim("acr", acr),
            new Claim("roles", "platform-admin"),
        ]);
        var cookie = AuthCookies.Protect(withEndpoints.Services, PlatformAuthentication.CookieScheme, user);
        using var client = withEndpoints.CreateClient(new()
        {
            BaseAddress = new Uri($"https://{PlatformWebFactory.PlatformHost}"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, PlatformEndpointStartupFilter.RoleOnlyPath).WithCookie("waslabid.platform", cookie), Ct);

        response.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public void Every_console_page_names_the_PlatformAdmin_policy()
    {
        // Inside a Blazor circuit there is no request for the host-aware default policy to read, so each routable
        // component under /platform must carry the policy itself.
        var pages = typeof(PlatformAuthentication).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => (Type: t, r.Template)))
            .Where(p => p.Template.TrimStart('/').Split('/')[0].Equals("platform", StringComparison.OrdinalIgnoreCase))
            .ToList();

        pages.Select(p => p.Type.Name).ShouldContain("HealthBoard");
        pages.Select(p => p.Type.Name).ShouldContain("Tenants");
        foreach (var (type, template) in pages)
        {
            type.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .Select(a => a.Policy)
                .ShouldContain(PlatformAuthentication.PolicyName, $"{type.FullName} ({template})");
        }
    }
}
