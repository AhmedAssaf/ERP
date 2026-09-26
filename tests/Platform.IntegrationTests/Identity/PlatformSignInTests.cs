using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_so_the_right_one_is_refused()
    {
        // Control: the right password alone moves this user past the password form (to its OTP setup).
        using (var control = new KeycloakBrowser(keycloak.BaseAddress))
        {
            var first = await control.OpenAsync(keycloak.ProbeAuthorizationUrl(), Ct);
            var accepted = await control.SubmitAsync(
                first, "kc-form-login", Credentials(PlatformKeycloakFixture.LockoutUser, PlatformKeycloakFixture.UserPassword), Ct);
            var next = accepted.Page.ShouldNotBeNull();
            KeycloakBrowser.HasForm(next, "kc-form-login").ShouldBeFalse(KeycloakBrowser.Feedback(next));
        }

        using var browser = new KeycloakBrowser(keycloak.BaseAddress);
        var page = await browser.OpenAsync(keycloak.ProbeAuthorizationUrl(), Ct);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var refused = await browser.SubmitAsync(
                page, "kc-form-login", Credentials(PlatformKeycloakFixture.LockoutUser, $"wrong-password-{attempt}"), Ct);
            page = refused.Page.ShouldNotBeNull();
            KeycloakBrowser.HasForm(page, "kc-form-login").ShouldBeTrue(KeycloakBrowser.Feedback(page));
        }

        var locked = await browser.SubmitAsync(
            page, "kc-form-login", Credentials(PlatformKeycloakFixture.LockoutUser, PlatformKeycloakFixture.UserPassword), Ct);

        locked.Callback.ShouldBeNull();
        var lockedPage = locked.Page.ShouldNotBeNull();
        KeycloakBrowser.HasForm(lockedPage, "kc-form-login").ShouldBeTrue(KeycloakBrowser.Feedback(lockedPage));
    }

    [Theory]
    [InlineData(PlatformKeycloakFixture.ProbeClientId, true)]
    [InlineData(PlatformKeycloakFixture.NoMinimumAcrProbeClientId, false)]
    public async Task Without_acr_values_the_clients_minimum_acr_alone_asks_for_the_code(string clientId, bool asksForCode)
    {
        // The request carries no acr_values. The probe client is the repository's web client without PAR; its control
        // copy also lacks minimum.acr.value, so the difference between the two outcomes is that attribute alone.
        using var browser = new KeycloakBrowser(keycloak.BaseAddress);
        var authorization = keycloak.ProbeAuthorizationUrl(clientId);
        authorization.Query.ShouldNotContain("acr_values");
        var login = await browser.OpenAsync(authorization, Ct);

        var step = await browser.SubmitAsync(
            login, "kc-form-login", Credentials(PlatformKeycloakFixture.OtpAdmin, PlatformKeycloakFixture.UserPassword), Ct);

        if (asksForCode)
        {
            step.Callback.ShouldBeNull("the password alone must not complete a login for this client");
            var page = step.Page.ShouldNotBeNull();
            KeycloakBrowser.HasForm(page, "kc-otp-login-form").ShouldBeTrue(KeycloakBrowser.Feedback(page));
        }
        else
        {
            step.Callback.ShouldNotBeNull(step.Page is null ? null : KeycloakBrowser.Feedback(step.Page))
                .Url.GetLeftPart(UriPartial.Path).ShouldBe(PlatformKeycloakFixture.ProbeRedirectUri);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sign_out_without_an_antiforgery_token_is_refused(bool platform)
    {
        var target = SignOutTarget.For(platform, keycloak);
        await using var factory = Factory();
        var cookie = AuthCookies.Protect(factory.Services, target.CookieScheme, target.User());
        using var client = Client(factory, target.Base, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, target.Path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>()),
        }.WithCookie(target.CookieName, cookie);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (response.Headers.TryGetValues("Set-Cookie", out var setCookies) ? setCookies : [])
            .ShouldNotContain(c => c.StartsWith(target.CookieName + "=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sign_out_clears_only_this_hosts_cookie_and_returns_home_through_the_realm(bool platform)
    {
        var target = SignOutTarget.For(platform, keycloak);
        var other = SignOutTarget.For(!platform, keycloak);
        await using var factory = Factory();
        using var client = Client(factory, target.Base, handleCookies: false);

        using var response = await PostSignOutAsync(factory, client, target, other);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var logout = response.Headers.Location.ShouldNotBeNull();
        logout.GetLeftPart(UriPartial.Path).ShouldBe($"{target.Authority}/protocol/openid-connect/logout");
        var query = QueryHelpers.ParseQuery(logout.Query);
        query["client_id"].ToString().ShouldBe(target.ClientId);
        query["post_logout_redirect_uri"].ToString().ShouldBe(target.SignedOutCallback);
        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        setCookies.ShouldContain(c => c.StartsWith(target.CookieName + "=;", StringComparison.Ordinal)
            && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        setCookies.ShouldNotContain(c => c.StartsWith(other.CookieName + "=", StringComparison.Ordinal));

        // Keycloak accepts the registered post-logout URI (this browser has no realm session, so no confirmation) and
        // hands back to it; the app then returns to the host's home.
        using var browser = new KeycloakBrowser(keycloak.BaseAddress);
        var step = await browser.NavigateAsync(logout, Ct);
        await ShouldReturnHomeAsync(client, target, step);
    }

    [Fact]
    public async Task A_platform_sign_out_ends_the_realm_session()
    {
        var target = SignOutTarget.For(platform: true, keycloak);
        await using var factory = Factory();
        using var client = Client(factory, target.Base, handleCookies: false);
        using var browser = new KeycloakBrowser(keycloak.BaseAddress);
        await SignInWithOtpAsync(browser, PlatformKeycloakFixture.SignOutAdmin);
        // Control: with the realm session alive, a new login skips the password and asks only for the code.
        (await browser.OpenAsync(keycloak.ProbeAuthorizationUrl(), Ct)).ShouldSatisfyAllConditions(
            page => KeycloakBrowser.HasForm(page, "kc-form-login").ShouldBeFalse(KeycloakBrowser.Feedback(page)),
            page => KeycloakBrowser.HasForm(page, "kc-otp-login-form").ShouldBeTrue(KeycloakBrowser.Feedback(page)));

        using var response = await PostSignOutAsync(factory, client, target, SignOutTarget.For(platform: false, keycloak));
        var step = await browser.NavigateAsync(response.Headers.Location.ShouldNotBeNull(), Ct);
        // Without an id token hint Keycloak asks the user to confirm before ending a live session.
        var confirm = step.Page.ShouldNotBeNull("Keycloak should ask to confirm the sign-out of a live session");
        await ShouldReturnHomeAsync(client, target, await browser.SubmitFirstFormAsync(confirm, Ct));

        var again = await browser.OpenAsync(keycloak.ProbeAuthorizationUrl(), Ct);
        KeycloakBrowser.HasForm(again, "kc-form-login").ShouldBeTrue(KeycloakBrowser.Feedback(again));
    }

    private async Task SignInWithOtpAsync(KeycloakBrowser browser, string username)
    {
        var login = await browser.OpenAsync(keycloak.ProbeAuthorizationUrl(), Ct);
        var otp = await browser.SubmitAsync(login, "kc-form-login", Credentials(username, PlatformKeycloakFixture.UserPassword), Ct);
        var otpPage = otp.Page.ShouldNotBeNull();
        var done = await browser.SubmitAsync(otpPage, "kc-otp-login-form", new Dictionary<string, string>
        {
            ["otp"] = PlatformKeycloakFixture.CurrentOtp(),
        }, Ct);
        done.Callback.ShouldNotBeNull(done.Page is null ? null : KeycloakBrowser.Feedback(done.Page))
            .Url.GetLeftPart(UriPartial.Path).ShouldBe(PlatformKeycloakFixture.ProbeRedirectUri);
    }

    /// <summary>Posts the sign-out form as the page would: this host's cookie, the other host's cookie, antiforgery tokens.</summary>
    private static async Task<HttpResponseMessage> PostSignOutAsync(
        WebApplicationFactory<Program> factory, HttpClient client, SignOutTarget target, SignOutTarget other)
    {
        var user = target.User();
        var cookie = AuthCookies.Protect(factory.Services, target.CookieScheme, user);
        var otherCookie = AuthCookies.Protect(factory.Services, other.CookieScheme, other.User());
        var antiforgery = AntiforgeryTokens(factory.Services, user);
        using var request = new HttpRequestMessage(HttpMethod.Post, target.Path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { [antiforgery.FormField] = antiforgery.RequestToken }),
        };
        request.Headers.Add(
            "Cookie", $"{target.CookieName}={cookie}; {other.CookieName}={otherCookie}; {antiforgery.CookieName}={antiforgery.CookieToken}");
        return await client.SendAsync(request, Ct);
    }

    private static async Task ShouldReturnHomeAsync(HttpClient client, SignOutTarget target, KeycloakStep step)
    {
        var callback = step.Callback.ShouldNotBeNull(step.Page is null ? null : KeycloakBrowser.Feedback(step.Page));
        callback.Url.GetLeftPart(UriPartial.Path).ShouldBe(target.SignedOutCallback);
        using var back = await client.GetAsync(new Uri(callback.Url.PathAndQuery, UriKind.Relative), Ct);
        back.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        back.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe(target.Home);
    }

    private static Dictionary<string, string> Credentials(string username, string password) =>
        new() { ["username"] = username, ["password"] = password };

    /// <summary>Antiforgery tokens for <paramref name="user"/>, as the page's sign-out form would carry them.</summary>
    private static (string CookieName, string CookieToken, string FormField, string RequestToken) AntiforgeryTokens(
        IServiceProvider services, ClaimsPrincipal user)
    {
        using var scope = services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = user };
        var tokens = scope.ServiceProvider.GetRequiredService<IAntiforgery>().GetTokens(context);
        var cookieName = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name!;
        return (cookieName, tokens.CookieToken!, tokens.FormFieldName, tokens.RequestToken!);
    }

    /// <summary>One host's sign-out: its endpoint, cookie, realm, and where the realm and then the app send the browser.</summary>
    private sealed record SignOutTarget(
        Uri Base, string Path, string CookieName, string CookieScheme, string Authority, string ClientId, string SignedOutCallback,
        string Home, Func<ClaimsPrincipal> User)
    {
        public static SignOutTarget For(bool platform, PlatformKeycloakFixture keycloak) => platform
            ? new(PlatformBase, "/platform/sign-out", PlatformCookie, PlatformAuthentication.CookieScheme, keycloak.PlatformAuthority,
                "waslabid-platform-web", "https://platform.localhost/signout-callback-platform", "/platform",
                () => AuthCookies.Principal(
                [
                    new Claim("sub", "platform.admin"),
                    new Claim("preferred_username", "platform.admin"),
                    new Claim("acr", "2"),
                    new Claim("roles", "platform-admin"),
                ]))
            : new(AcmeBase, "/account/sign-out", TenantCookie, CookieAuthenticationDefaults.AuthenticationScheme, keycloak.TenantAuthority,
                "waslabid-web", "https://acme.localhost/signout-callback-oidc", "/",
                () => AuthCookies.Principal(
                [
                    new Claim("sub", "acme.admin"),
                    new Claim("preferred_username", "acme.admin"),
                    new Claim("organization", "acme"),
                ]));
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
