using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.IntegrationTests.Infrastructure;
using Platform.IntegrationTests.Web;
using Platform.Web.PlatformHost;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// The platform console's sign-in against a real Keycloak with both realms (plan task 6, D-1, D-2): the platform realm
/// challenges, an OTP login carries acr 2 and passes the PlatformAdmin policy, a password-only session does not, and
/// the tenant and platform cookies are each refused on the other's host.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PlatformSignInTests(DatabaseFixture db, PlatformKeycloakFixture keycloak) : IClassFixture<PlatformKeycloakFixture>
{
    private const string TenantCookie = "waslabid.auth";
    private const string PlatformCookie = "waslabid.platform";
    private static readonly Uri PlatformBase = new($"https://{PlatformWebFactory.PlatformHost}");
    private static readonly Uri AcmeBase = new("https://acme.localhost");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/platform")]
    [InlineData(PlatformEndpointStartupFilter.DefaultPolicyPath)]
    [InlineData(PlatformEndpointStartupFilter.FallbackPolicyPath)]
    public async Task An_anonymous_platform_request_is_challenged_by_the_platform_realm(string path)
    {
        OpenIdConnectMessage? pushed = null;
        await using var factory = Factory(services => services.PostConfigure<OpenIdConnectOptions>(PlatformAuthentication.OidcScheme, options =>
            options.Events.OnPushAuthorization = context =>
            {
                pushed = context.ProtocolMessage;
                return Task.CompletedTask;
            }));
        using var client = Client(factory, PlatformBase);

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = response.Headers.Location.ShouldNotBeNull();
        location.GetLeftPart(UriPartial.Path).ShouldBe($"{keycloak.PlatformAuthority}/protocol/openid-connect/auth");
        QueryHelpers.ParseQuery(location.Query)["client_id"].ToString().ShouldBe("waslabid-platform-web");
        pushed.ShouldNotBeNull();
        pushed.ResponseType.ShouldBe("code");
        pushed.GetParameter("code_challenge_method").ShouldBe("S256");
        pushed.RedirectUri.ShouldBe("https://platform.localhost/signin-platform");
        pushed.AcrValues.ShouldBe("2");
    }

    [Theory]
    [InlineData(PlatformEndpointStartupFilter.DefaultPolicyPath)]
    [InlineData(PlatformEndpointStartupFilter.FallbackPolicyPath)]
    public async Task A_password_only_session_is_refused_by_the_platform_policy(string path)
    {
        var token = new JsonWebToken(await keycloak.PasswordGrantIdTokenAsync(PlatformKeycloakFixture.PasswordOnlyAdmin, Ct));
        // The refusal must come from the acr level alone: the user holds the role and the session is otherwise valid.
        token.GetClaim("acr").Value.ShouldBe("1");
        token.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ShouldContain("platform-admin");
        await using var factory = Factory();
        var cookie = AuthCookies.Protect(factory.Services, PlatformAuthentication.CookieScheme, AuthCookies.Principal(token.Claims));
        using var client = Client(factory, PlatformBase, handleCookies: false);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).WithCookie(PlatformCookie, cookie), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_otp_login_carries_acr_2_and_passes_the_policy()
    {
        ClaimsPrincipal? validated = null;
        await using var factory = Factory(services => services.PostConfigure<OpenIdConnectOptions>(PlatformAuthentication.OidcScheme, options =>
            options.Events.OnTokenValidated = context =>
            {
                validated = context.Principal;
                return Task.CompletedTask;
            }));
        using var client = Client(factory, PlatformBase);
        using var browser = new KeycloakBrowser(keycloak.BaseAddress);

        using var challenge = await client.GetAsync(new Uri(PlatformEndpointStartupFilter.DefaultPolicyPath, UriKind.Relative), Ct);
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var loginPage = await browser.OpenAsync(challenge.Headers.Location.ShouldNotBeNull(), Ct);
        var afterPassword = await browser.SubmitAsync(loginPage, "kc-form-login", new Dictionary<string, string>
        {
            ["username"] = PlatformKeycloakFixture.OtpAdmin,
            ["password"] = PlatformKeycloakFixture.UserPassword,
        }, Ct);
        afterPassword.Callback.ShouldBeNull("the password alone must not complete a platform login");
        var otpPage = afterPassword.Page.ShouldNotBeNull();
        KeycloakBrowser.HasForm(otpPage, "kc-otp-login-form").ShouldBeTrue(KeycloakBrowser.Feedback(otpPage));
        var afterOtp = await browser.SubmitAsync(otpPage, "kc-otp-login-form", new Dictionary<string, string>
        {
            ["otp"] = PlatformKeycloakFixture.CurrentOtp(),
        }, Ct);
        var callback = afterOtp.Callback.ShouldNotBeNull(afterOtp.Page is null ? null : KeycloakBrowser.Feedback(afterOtp.Page));
        callback.Url.GetLeftPart(UriPartial.Path).ShouldBe("https://platform.localhost/signin-platform");

        using var callbackRequest = new HttpRequestMessage(callback.Method, callback.Url.PathAndQuery);
        if (callback.Method == HttpMethod.Post)
        {
            callbackRequest.Content = new FormUrlEncodedContent(callback.Form);
        }

        using var signedIn = await client.SendAsync(callbackRequest, Ct);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        validated.ShouldNotBeNull();
        validated.FindAll("acr").Select(c => c.Value).ShouldBe(["2"]);
        validated.FindAll("roles").Select(c => c.Value).ShouldContain("platform-admin");
        signedIn.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith(PlatformCookie + "=", StringComparison.Ordinal));
        using var page = await client.GetAsync(signedIn.Headers.Location.ShouldNotBeNull(), Ct);

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync(Ct)).ShouldBe("2");
    }

    [Fact]
    public async Task A_tenant_cookie_is_not_accepted_on_the_platform_host()
    {
        await using var factory = Factory();
        var acmeAdmin = AuthCookies.Principal(
        [
            new Claim("sub", "acme.admin"),
            new Claim("preferred_username", "acme.admin"),
            new Claim("organization", "acme"),
            new Claim("acr", "2"),
            new Claim("roles", "platform-admin"),
        ]);
        var cookie = AuthCookies.Protect(factory.Services, CookieAuthenticationDefaults.AuthenticationScheme, acmeAdmin);

        // Control: the cookie is a valid session on its own host.
        using (var own = await GetAsync(factory, AcmeBase, "/", TenantCookie, cookie))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        ShouldBeChallengedBy(
            await GetAsync(factory, PlatformBase, PlatformEndpointStartupFilter.DefaultPolicyPath, TenantCookie, cookie), keycloak.PlatformAuthority);
        ShouldBeChallengedBy(
            await GetAsync(factory, PlatformBase, PlatformEndpointStartupFilter.DefaultPolicyPath, PlatformCookie, cookie), keycloak.PlatformAuthority);
    }

    [Fact]
    public async Task A_platform_cookie_is_not_accepted_on_a_tenant_host()
    {
        await using var factory = Factory();
        var platformAdmin = AuthCookies.Principal(
        [
            new Claim("sub", "platform.admin"),
            new Claim("preferred_username", "platform.admin"),
            new Claim("acr", "2"),
            new Claim("roles", "platform-admin"),
            new Claim("organization", "acme"),
        ]);
        var cookie = AuthCookies.Protect(factory.Services, PlatformAuthentication.CookieScheme, platformAdmin);

        // Control: the cookie is a valid session on its own host.
        using (var own = await GetAsync(factory, PlatformBase, PlatformEndpointStartupFilter.DefaultPolicyPath, PlatformCookie, cookie))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        ShouldBeChallengedBy(await GetAsync(factory, AcmeBase, "/", PlatformCookie, cookie), keycloak.TenantAuthority);
        ShouldBeChallengedBy(await GetAsync(factory, AcmeBase, "/", TenantCookie, cookie), keycloak.TenantAuthority);
    }

    private WebApplicationFactory<Program> Factory(Action<IServiceCollection>? configure = null) =>
        new PlatformWebFactory(db.AppConnectionString, keycloak.OidcSettings).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, PlatformEndpointStartupFilter>();
                configure?.Invoke(services);
            }));

    private static HttpClient Client(WebApplicationFactory<Program> factory, Uri baseAddress, bool handleCookies = true) =>
        factory.CreateClient(new() { BaseAddress = baseAddress, AllowAutoRedirect = false, HandleCookies = handleCookies });

    private static async Task<HttpResponseMessage> GetAsync(
        WebApplicationFactory<Program> factory, Uri baseAddress, string path, string cookieName, string cookieValue)
    {
        using var client = Client(factory, baseAddress, handleCookies: false);
        return await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).WithCookie(cookieName, cookieValue), Ct);
    }

    private static void ShouldBeChallengedBy(HttpResponseMessage response, string authority)
    {
        using (response)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            response.Headers.Location.ShouldNotBeNull().GetLeftPart(UriPartial.Path).ShouldBe($"{authority}/protocol/openid-connect/auth");
        }
    }
}
