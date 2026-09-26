using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Platform.Modules.Identity.Keycloak;

namespace Platform.UnitTests.Identity;

/// <summary>
/// The Keycloak Admin API client (plan task 9, D-4) against a scripted HTTP handler: the service account's token is
/// fetched once and reused until shortly before it expires, and the invitation email names the web client, the tenant
/// host and a 72-hour lifespan.
/// </summary>
public sealed class KeycloakAdminClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_service_account_token_is_reused_until_it_is_about_to_expire()
    {
        var handler = new ScriptedHandler();
        var clock = new ManualClock();
        var state = new KeycloakAdminState(clock);

        await Client(handler, state).CountOrganizationMembersAsync("acme", Ct);
        await Client(handler, state).CountOrganizationMembersAsync("acme", Ct);
        handler.TokenRequests.ShouldBe(1);

        // Expires in 300 s; renewed once less than 30 s remain.
        clock.Advance(TimeSpan.FromSeconds(269));
        await Client(handler, state).CountOrganizationMembersAsync("acme", Ct);
        handler.TokenRequests.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(2));
        await Client(handler, state).CountOrganizationMembersAsync("acme", Ct);
        handler.TokenRequests.ShouldBe(2);
        handler.Requests.Where(r => r.Contains("/members/count", StringComparison.Ordinal)).ShouldAllBe(r => r.Contains("Bearer token-", StringComparison.Ordinal));
        // The organization id is looked up once per alias.
        handler.Requests.Count(r => r.StartsWith("GET /admin/realms/waslabid/organizations?", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task The_invitation_email_names_the_web_client_the_tenant_host_and_72_hours()
    {
        var handler = new ScriptedHandler();
        var client = Client(handler, new KeycloakAdminState(new ManualClock()));

        await client.SendInvitationEmailAsync("user-1", ["UPDATE_PASSWORD", "CONFIGURE_TOTP"], new Uri("https://acme.localhost:8443/"), Ct);

        var request = handler.Requests.Single(r => r.StartsWith("PUT ", StringComparison.Ordinal));
        request.ShouldStartWith("PUT /admin/realms/waslabid/users/user-1/execute-actions-email?");
        request.ShouldContain("client_id=waslabid-web");
        request.ShouldContain("redirect_uri=https%3A%2F%2Facme.localhost%3A8443%2F");
        request.ShouldContain("lifespan=259200");
        request.ShouldContain("[\"UPDATE_PASSWORD\",\"CONFIGURE_TOTP\"]");
    }

    /// <summary>
    /// QA pass, F-06: <see cref="KeycloakAdminException.DuringUserCreation"/> is true only when it is the create-user
    /// request itself that Keycloak refused with 400, so <c>StaffService</c> can tell that apart from a 400 raised
    /// while renewing the service account's own token mid-call (its credentials stop working, say), which is not about
    /// the person's name at all.
    /// </summary>
    [Fact]
    public async Task A_400_creating_the_user_is_marked_as_during_user_creation()
    {
        var client = TokenThenCreateUserClient(
            token: _ => Ok("""{"access_token":"token","expires_in":300,"token_type":"Bearer"}"""),
            createUser: _ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        var ex = await Should.ThrowAsync<KeycloakAdminException>(
            () => client.CreateUserAsync(new NewKeycloakUser("sara@acme.example.sa", "Sara", "Ahmed", "en"), Ct));

        ex.Status.ShouldBe(HttpStatusCode.BadRequest);
        ex.DuringUserCreation.ShouldBeTrue();
    }

    [Fact]
    public async Task A_400_renewing_the_token_mid_call_is_not_marked_as_during_user_creation()
    {
        // The create-user attempt is answered 401 as if Keycloak had just revoked the cached token; the client asks
        // for a new one, and that second token request is refused with 400 before the create-user request is ever
        // retried, so its body (with the name) is never the thing Keycloak refused.
        var tokenCalls = 0;
        var client = TokenThenCreateUserClient(
            token: _ => ++tokenCalls == 1
                ? Ok("""{"access_token":"token","expires_in":300,"token_type":"Bearer"}""")
                : new HttpResponseMessage(HttpStatusCode.BadRequest),
            createUser: _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ex = await Should.ThrowAsync<KeycloakAdminException>(
            () => client.CreateUserAsync(new NewKeycloakUser("sara@acme.example.sa", "Sara", "Ahmed", "en"), Ct));

        ex.Status.ShouldBe(HttpStatusCode.BadRequest);
        ex.DuringUserCreation.ShouldBeFalse();
    }

    private static KeycloakAdminClient TokenThenCreateUserClient(
        Func<HttpRequestMessage, HttpResponseMessage> token, Func<HttpRequestMessage, HttpResponseMessage> createUser)
    {
        var http = new HttpClient(new TokenAndCreateUserHandler(token, createUser)) { BaseAddress = new Uri("http://keycloak.test/") };
        var options = Options.Create(new KeycloakAdminOptions { BaseUrl = "http://keycloak.test", ClientSecret = "secret" });
        return new KeycloakAdminClient(http, new KeycloakAdminState(new ManualClock()), options);
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Answers the token endpoint and the create-user endpoint from separate callbacks; everything else is 204.</summary>
    private sealed class TokenAndCreateUserHandler(
        Func<HttpRequestMessage, HttpResponseMessage> token, Func<HttpRequestMessage, HttpResponseMessage> createUser) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
            {
                return Task.FromResult(token(request));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/users", StringComparison.Ordinal))
            {
                return Task.FromResult(createUser(request));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private static KeycloakAdminClient Client(ScriptedHandler handler, KeycloakAdminState state) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://keycloak.test/") },
            state,
            Options.Create(new KeycloakAdminOptions
            {
                BaseUrl = "http://keycloak.test",
                ClientSecret = "secret",
                TenantUrl = "https://{slug}.localhost:8443/",
            }));

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private int _tokens;

        public int TokenRequests => _tokens;

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery} {request.Headers.Authorization} {body}");
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
            {
                var number = Interlocked.Increment(ref _tokens);
                return Json($$"""{"access_token":"token-{{number}}","expires_in":300,"token_type":"Bearer"}""");
            }

            if (path.EndsWith("/organizations", StringComparison.Ordinal))
            {
                return Json("""[{"id":"org-1","alias":"acme","name":"Acme"}]""");
            }

            if (path.EndsWith("/members/count", StringComparison.Ordinal))
            {
                return Json("3");
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
