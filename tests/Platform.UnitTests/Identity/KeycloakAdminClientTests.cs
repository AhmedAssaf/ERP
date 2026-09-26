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
