using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.IntegrationTests.Infrastructure;
using Platform.IntegrationTests.Web;
using Platform.Modules.Audit;
using Platform.Shared.Tenancy;
using Platform.Web.PlatformHost;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// W-21 against a real Keycloak 26.3 with the repository's tenant realm: the tenant cookie's principal is revalidated
/// against the host tenant's organization through the Admin API (the app's own service account). A member removed from
/// the organization, or whose account is disabled, is challenged within five minutes and the end of the session is
/// audited once in that tenant's log; a member confirmed within two minutes is not a Keycloak call per request; a user
/// removed from one tenant's organization keeps the other tenant's session (ADR-0008). Time is the host's
/// <see cref="TimeProvider"/>, moved by the test; the OpenID Connect configuration is fixed, so a challenge is an
/// observable redirect without a network. Each test uses its own Keycloak user.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MembershipRevalidationTests(DatabaseFixture db, KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    internal const string TenantRealmAuthorize = "https://tenant-realm.invalid/auth";
    private const string TenantCookie = "waslabid.auth";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_member_removed_from_the_organization_is_challenged_within_five_minutes_and_audited_once()
    {
        var userId = await NewUserInAsync(["acme"]);
        var clock = new TestClock();
        await using var factory = Factory(db, clock, keycloak.BaseAddress);
        var session = Session(factory, userId, "acme", clock.GetUtcNow());
        using var client = Client(factory, "acme.localhost");

        (await GetAsync(client, session)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // Past the sign-in's own proof: Keycloak is asked and confirms the membership.
        clock.Advance(TimeSpan.FromMinutes(3));
        (await GetAsync(client, session)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await RemoveAsync("acme", userId);
        // The confirmation is reused for two minutes, so not every request asks Keycloak.
        (await GetAsync(client, session)).StatusCode.ShouldBe(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(2));
        using var challenged = await GetAsync(client, session);

        ShouldBeChallenged(challenged);
        challenged.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith($"{TenantCookie}=;", StringComparison.Ordinal), "the cookie is deleted");
        ShouldBeChallenged(await GetAsync(client, session));
        var revoked = await RevokedRowsAsync(TestTenants.Acme, userId);
        var row = revoked.ShouldHaveSingleItem();
        row.ActorId.ShouldBe(userId);
        row.SubjectType.ShouldBe("user");
        using var data = JsonDocument.Parse(row.Data);
        data.RootElement.GetProperty("organization").GetString().ShouldBe("acme");
        data.RootElement.GetProperty("reason").GetString().ShouldBe("removed_from_organization");
        data.RootElement.GetProperty("session").GetString().ShouldBe("staff");
    }

    [Fact]
    public async Task A_disabled_account_is_challenged_and_audited_like_a_removed_member()
    {
        var userId = await NewUserInAsync(["acme"]);
        var clock = new TestClock();
        await using var factory = Factory(db, clock, keycloak.BaseAddress);
        var session = Session(factory, userId, "acme", clock.GetUtcNow());
        using var client = Client(factory, "acme.localhost");

        await keycloak.AdminSendAsync(HttpMethod.Put, $"users/{userId}", new { enabled = false }, Ct);
        clock.Advance(TimeSpan.FromMinutes(2));

        ShouldBeChallenged(await GetAsync(client, session));
        using var data = JsonDocument.Parse((await RevokedRowsAsync(TestTenants.Acme, userId)).ShouldHaveSingleItem().Data);
        data.RootElement.GetProperty("reason").GetString().ShouldBe("account_disabled");
    }

    [Fact]
    public async Task A_user_removed_from_one_organization_keeps_the_session_of_the_other_tenant()
    {
        // ADR-0008: one identity, membership per tenant. Each host has its own cookie, carrying its own organization.
        var userId = await NewUserInAsync(["acme", "beta"]);
        var clock = new TestClock();
        await using var factory = Factory(db, clock, keycloak.BaseAddress);
        var onAcme = Session(factory, userId, "acme", clock.GetUtcNow());
        var onBeta = Session(factory, userId, "beta", clock.GetUtcNow());
        using var acme = Client(factory, "acme.localhost");
        using var beta = Client(factory, "beta.localhost");

        await RemoveAsync("beta", userId);
        clock.Advance(TimeSpan.FromMinutes(2));

        (await GetAsync(acme, onAcme)).StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldBeChallenged(await GetAsync(beta, onBeta));
        (await RevokedRowsAsync(TestTenants.Beta, userId)).ShouldHaveSingleItem();
        (await RevokedRowsAsync(TestTenants.Acme, userId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_member_signed_in_again_after_being_added_back_is_not_refused_by_the_earlier_removal()
    {
        var userId = await NewUserInAsync(["acme"]);
        var clock = new TestClock();
        await using var factory = Factory(db, clock, keycloak.BaseAddress);
        var first = Session(factory, userId, "acme", clock.GetUtcNow());
        using var client = Client(factory, "acme.localhost");
        await RemoveAsync("acme", userId);
        clock.Advance(TimeSpan.FromMinutes(2));
        ShouldBeChallenged(await GetAsync(client, first));

        await keycloak.AdminSendAsync(HttpMethod.Post, $"organizations/{await keycloak.OrganizationIdAsync("acme", Ct)}/members", userId, Ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = Session(factory, userId, "acme", clock.GetUtcNow());

        (await GetAsync(client, second)).StatusCode.ShouldBe(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(3));
        (await GetAsync(client, second)).StatusCode.ShouldBe(HttpStatusCode.OK, "Keycloak confirms the membership again");
    }

    internal static WebApplicationFactory<Program> Factory(DatabaseFixture db, TestClock clock, string keycloakAdminBaseUrl) =>
        new PlatformWebFactory(db.AppConnectionString, new OidcSettings("https://tenant-realm.invalid/realms/waslabid", "unused-in-tests"))
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("KeycloakAdmin:BaseUrl", keycloakAdminBaseUrl);
                builder.UseSetting("KeycloakAdmin:ClientSecret", KeycloakFixture.AdminApiSecret);
                builder.UseSetting("KeycloakAdmin:TenantUrl", "https://{slug}.localhost:8443/");
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<TimeProvider>(clock);
                    services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                    {
                        o.Configuration = new OpenIdConnectConfiguration { AuthorizationEndpoint = TenantRealmAuthorize };
                        o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(o.Configuration);
                    });
                });
            });

    internal static string Session(WebApplicationFactory<Program> factory, string userId, string organization, DateTimeOffset signedInAt) =>
        AuthCookies.Protect(
            factory.Services,
            CookieAuthenticationDefaults.AuthenticationScheme,
            AuthCookies.Principal([new Claim("sub", userId), new Claim("preferred_username", userId), new Claim("organization", organization)]),
            signedInAt);

    internal static HttpClient Client(WebApplicationFactory<Program> factory, string host) =>
        factory.CreateClient(new() { BaseAddress = new Uri($"https://{host}"), AllowAutoRedirect = false, HandleCookies = false });

    internal static Task<HttpResponseMessage> GetAsync(HttpClient client, string session, string path = "/") =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).WithCookie(TenantCookie, session), Ct);

    internal static void ShouldBeChallenged(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location.ShouldNotBeNull().AbsoluteUri.ShouldStartWith(TenantRealmAuthorize, customMessage: "a sign-in challenge at the tenant realm");
    }

    internal static async Task<List<AuditEvent>> RevokedRowsAsync(DatabaseFixture db, TenantContext tenant, string userId)
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(tenant);
        await using var audit = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuditDbContext>>().CreateDbContextAsync(Ct);
        return await audit.Events.AsNoTracking()
            .Where(e => e.Action == "identity.session_revoked" && e.SubjectId == userId)
            .ToListAsync(Ct);
    }

    private Task<List<AuditEvent>> RevokedRowsAsync(TenantContext tenant, string userId) => RevokedRowsAsync(db, tenant, userId);

    private async Task<string> NewUserInAsync(IReadOnlyList<string> organizations)
    {
        var email = $"w21.{Guid.NewGuid():N}@acme.waslabid.test";
        var userId = await keycloak.CreateUserAsync(email, KeycloakFixture.UserPassword, emailVerified: true, Ct);
        foreach (var alias in organizations)
        {
            await keycloak.AdminSendAsync(HttpMethod.Post, $"organizations/{await keycloak.OrganizationIdAsync(alias, Ct)}/members", userId, Ct);
        }

        return userId;
    }

    private async Task RemoveAsync(string alias, string userId) =>
        await keycloak.AdminSendAsync(HttpMethod.Delete, $"organizations/{await keycloak.OrganizationIdAsync(alias, Ct)}/members/{userId}", null, Ct);
}

/// <summary>
/// W-21 when Keycloak cannot answer (the Admin API points at a closed port): a session whose membership was confirmed
/// within ten minutes is kept, an older one is challenged, and nothing is audited, since nothing is known about the
/// membership. The platform console's session is never revalidated against a tenant organization.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MembershipRevalidationOutageTests(DatabaseFixture db)
{
    private const string Unreachable = "http://127.0.0.1:9";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task When_keycloak_cannot_answer_a_session_signed_in_within_ten_minutes_is_kept_and_an_older_one_is_challenged()
    {
        var userId = $"w21.outage.{Guid.NewGuid():N}";
        var clock = new TestClock();
        await using var factory = MembershipRevalidationTests.Factory(db, clock, Unreachable);
        var session = MembershipRevalidationTests.Session(factory, userId, "acme", clock.GetUtcNow());
        using var client = MembershipRevalidationTests.Client(factory, "acme.localhost");

        clock.Advance(TimeSpan.FromMinutes(9));
        (await MembershipRevalidationTests.GetAsync(client, session)).StatusCode.ShouldBe(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(1));

        MembershipRevalidationTests.ShouldBeChallenged(await MembershipRevalidationTests.GetAsync(client, session));
        (await MembershipRevalidationTests.RevokedRowsAsync(db, TestTenants.Acme, userId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cookie_without_a_sign_in_stamp_is_challenged_while_keycloak_cannot_answer()
    {
        var clock = new TestClock();
        await using var factory = MembershipRevalidationTests.Factory(db, clock, Unreachable);
        var session = AuthCookies.ProtectWithoutSignInStamp(
            factory.Services,
            CookieAuthenticationDefaults.AuthenticationScheme,
            AuthCookies.Principal([new Claim("sub", "w21.unstamped"), new Claim("organization", "acme")]));
        using var client = MembershipRevalidationTests.Client(factory, "acme.localhost");

        MembershipRevalidationTests.ShouldBeChallenged(await MembershipRevalidationTests.GetAsync(client, session));
    }

    [Fact]
    public async Task The_platform_console_session_is_not_revalidated_against_a_tenant_organization()
    {
        var clock = new TestClock();
        await using var factory = MembershipRevalidationTests.Factory(db, clock, Unreachable)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, PlatformEndpointStartupFilter>()));
        var admin = AuthCookies.Principal(
        [
            new Claim("sub", "platform.admin"),
            new Claim("preferred_username", "platform.admin"),
            new Claim("acr", "2"),
            new Claim("roles", "platform-admin"),
        ]);
        var session = AuthCookies.ProtectWithoutSignInStamp(factory.Services, PlatformAuthentication.CookieScheme, admin);
        using var client = MembershipRevalidationTests.Client(factory, PlatformWebFactory.PlatformHost);

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, PlatformEndpointStartupFilter.DefaultPolicyPath).WithCookie(PlatformAuthentication.CookieName, session), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

/// <summary>The host's clock in W-21 tests: starts at the real time (cookie expiry is checked against it) and moves on demand.</summary>
internal sealed class TestClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
