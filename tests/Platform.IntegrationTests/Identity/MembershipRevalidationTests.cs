using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.IntegrationTests.Infrastructure;
using Platform.IntegrationTests.Web;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.Account;
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
/// within three and a half minutes is kept, an older one is challenged, and nothing is audited, since nothing is known about the
/// membership. The platform console's session is never revalidated against a tenant organization.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MembershipRevalidationOutageTests(DatabaseFixture db)
{
    private const string Unreachable = "http://127.0.0.1:9";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task When_keycloak_cannot_answer_a_session_signed_in_within_three_and_a_half_minutes_is_kept_and_an_older_one_is_challenged()
    {
        var userId = $"w21.outage.{Guid.NewGuid():N}";
        var clock = new TestClock();
        await using var factory = MembershipRevalidationTests.Factory(db, clock, Unreachable);
        var session = MembershipRevalidationTests.Session(factory, userId, "acme", clock.GetUtcNow());
        using var client = MembershipRevalidationTests.Client(factory, "acme.localhost");

        clock.Advance(TimeSpan.FromMinutes(3));
        (await MembershipRevalidationTests.GetAsync(client, session)).StatusCode.ShouldBe(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromSeconds(30));

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

/// <summary>
/// W-21 review: the end of a session is audited even when the request whose check found the removal ended (its scope,
/// with its database context factory, disposed) before Keycloak answered. The sign-in stamp is the token's own issue time
/// when the token carries one, since that is when Keycloak vouched for the organization.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MembershipRevalidationAuditTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_removal_found_by_a_request_that_ended_before_keycloak_answered_is_still_audited()
    {
        var source = new GatedSource();
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IOrganizationMembershipSource>(source))));
        var subject = $"w21.ended.{Guid.NewGuid():N}";
        Task<bool> check;
        await using (var request = factory.Services.CreateAsyncScope())
        {
            request.ServiceProvider.GetRequiredService<TenantAccessor>().Set(TestTenants.Acme);
            var revalidation = request.ServiceProvider.GetRequiredService<IMembershipRevalidation>();
            check = revalidation.IsStillMemberAsync(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject), new Claim("organization", "acme")], "test")), null, Ct);
            await source.Asked.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        source.Answer(OrganizationMembership.NotMember);

        (await check.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeFalse();
        (await MembershipRevalidationTests.RevokedRowsAsync(db, TestTenants.Acme, subject)).ShouldHaveSingleItem().ActorId.ShouldBe(subject);
    }

    [Fact]
    public void The_sign_in_stamp_is_the_tokens_issue_time_when_the_token_carries_one()
    {
        var now = new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);
        var issued = now.AddSeconds(-7);
        var withIat = new ClaimsPrincipal(new ClaimsIdentity([new Claim("iat", issued.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))], "test"));
        var fromTheFuture = new ClaimsPrincipal(new ClaimsIdentity([new Claim("iat", now.AddMinutes(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))], "test"));

        MembershipRevalidation.SignInTime(withIat, now).ShouldBe(issued);
        MembershipRevalidation.SignInTime(fromTheFuture, now).ShouldBe(now, customMessage: "a token time later than the sign-in is never trusted");
        MembershipRevalidation.SignInTime(new ClaimsPrincipal(new ClaimsIdentity()), now).ShouldBe(now);
    }

    [Fact]
    public async Task The_tenant_scheme_keeps_the_tokens_issue_time_on_the_principal()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString, new OidcSettings("https://keycloak.invalid/realms/waslabid", "unused-in-tests"));

        var oidc = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);

        oidc.ClaimActions.OfType<DeleteClaimAction>().ShouldNotContain(a => a.ClaimType == "iat");
    }

    private sealed class GatedSource : IOrganizationMembershipSource
    {
        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<OrganizationMembership> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Asked => _asked.Task;

        public void Answer(OrganizationMembership answer) => _answer.TrySetResult(answer);

        public Task<OrganizationMembership> CheckAsync(string organizationAlias, string userId, CancellationToken cancellationToken)
        {
            _asked.TrySetResult();
            return _answer.Task;
        }
    }
}
