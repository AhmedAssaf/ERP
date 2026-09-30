using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Platform.Modules.Identity;
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

        await Client(handler, state).CountOrganizationUsersAsync("acme", Ct);
        await Client(handler, state).CountOrganizationUsersAsync("acme", Ct);
        handler.TokenRequests.ShouldBe(1);

        // Expires in 300 s; renewed once less than 30 s remain.
        clock.Advance(TimeSpan.FromSeconds(269));
        await Client(handler, state).CountOrganizationUsersAsync("acme", Ct);
        handler.TokenRequests.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(2));
        await Client(handler, state).CountOrganizationUsersAsync("acme", Ct);
        handler.TokenRequests.ShouldBe(2);
        handler.Requests.Where(r => r.Contains("/members/count", StringComparison.Ordinal)).ShouldAllBe(r => r.Contains("Bearer token-", StringComparison.Ordinal));
        // The organization id is looked up once per alias.
        handler.Requests.Count(r => r.StartsWith("GET /admin/realms/waslabid/organizations?", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task The_user_count_of_an_organization_leaves_out_members_holding_the_vendor_role()
    {
        // F-54: vendors join the tenant's organization (V-3) but are not the tenant's users. 150 members, so the member
        // list takes two pages; three hold the vendor role, and one vendor of another organization does not count at all.
        var members = Enumerable.Range(1, 150).Select(i => $"u{i}").ToList();
        var handler = new ScriptedHandler { Members = members, VendorUsers = ["u2", "u101", "u150", "elsewhere"] };

        var count = await Client(handler, new KeycloakAdminState(new ManualClock())).CountOrganizationUsersAsync("acme", Ct);

        count.ShouldBe(147);
        handler.Requests.ShouldContain(r => r.StartsWith("GET /admin/realms/waslabid/roles/vendor/users?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_vendors_the_user_count_is_the_member_count()
    {
        var handler = new ScriptedHandler();

        (await Client(handler, new KeycloakAdminState(new ManualClock())).CountOrganizationUsersAsync("acme", Ct)).ShouldBe(3);
        (await Client(handler, new KeycloakAdminState(new ManualClock())).CountOrganizationUsersAsync("no-such-organization", Ct)).ShouldBeNull();
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

    [Fact]
    public async Task The_membership_check_reads_one_member_of_the_organization_and_its_enabled_flag()
    {
        // W-21: 200 with enabled true is a member, enabled false a disabled account, 404 not a member (or no such user),
        // an organization that does not exist has no members, and any other status is Keycloak failing, never a removal.
        var handler = new ScriptedHandler { MemberAnswers = { ["u1"] = "true", ["u2"] = "false", ["u9"] = "500" } };
        var client = Client(handler, new KeycloakAdminState(new ManualClock()));

        (await client.MembershipAsync("acme", "u1", Ct)).ShouldBe(OrganizationMembership.Member);
        (await client.MembershipAsync("acme", "u2", Ct)).ShouldBe(OrganizationMembership.Disabled);
        (await client.MembershipAsync("acme", "gone", Ct)).ShouldBe(OrganizationMembership.NotMember);
        (await client.MembershipAsync("no-such-organization", "u1", Ct)).ShouldBe(OrganizationMembership.NotMember);
        (await Should.ThrowAsync<KeycloakAdminException>(() => client.MembershipAsync("acme", "u9", Ct))).Status.ShouldBe(HttpStatusCode.InternalServerError);
        handler.Requests.ShouldContain(r => r.StartsWith("GET /admin/realms/waslabid/organizations/org-1/members/u1 Bearer token-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_account_check_reads_the_users_enabled_flag()
    {
        // W-21, P-2: a session that claims no organization of its host is still refused once the account is disabled.
        var handler = new ScriptedHandler { AccountAnswers = { ["u1"] = "true", ["u2"] = "false", ["u9"] = "503" } };
        var client = Client(handler, new KeycloakAdminState(new ManualClock()));

        (await client.AccountAsync("u1", Ct)).ShouldBe(OrganizationMembership.Member);
        (await client.AccountAsync("u2", Ct)).ShouldBe(OrganizationMembership.Disabled);
        (await client.AccountAsync("gone", Ct)).ShouldBe(OrganizationMembership.NotMember);
        (await Should.ThrowAsync<KeycloakAdminException>(() => client.AccountAsync("u9", Ct))).Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        handler.Requests.ShouldContain(r => r.StartsWith("GET /admin/realms/waslabid/users/u1 Bearer token-", StringComparison.Ordinal));
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

        /// <summary>Ids of the organization's members, served a page at a time.</summary>
        public List<string> Members { get; init; } = ["u1", "u2", "u3"];

        /// <summary>Ids of the users holding the realm role vendor, served a page at a time.</summary>
        public List<string> VendorUsers { get; init; } = [];

        /// <summary>Per user id: "true" or "false" (its enabled flag), or a status code; any other id is 404.</summary>
        public Dictionary<string, string> AccountAnswers { get; } = [];

        /// <summary>Per member id: "true" or "false" (its enabled flag), or a status code; any other id is 404.</summary>
        public Dictionary<string, string> MemberAnswers { get; } = [];

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
                return Json(Members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (path.EndsWith("/organizations/org-1/members", StringComparison.Ordinal))
            {
                return Page(Members, request.RequestUri);
            }

            const string memberPath = "/organizations/org-1/members/";
            if (path.Contains(memberPath, StringComparison.Ordinal) && !path.EndsWith("/count", StringComparison.Ordinal))
            {
                var id = path[(path.IndexOf(memberPath, StringComparison.Ordinal) + memberPath.Length)..];
                return MemberAnswers.TryGetValue(id, out var answer)
                    ? answer is "true" or "false"
                        ? Json($$"""{"id":"{{id}}","username":"{{id}}","enabled":{{answer}},"membershipType":"MANAGED"}""")
                        : new HttpResponseMessage((HttpStatusCode)int.Parse(answer, System.Globalization.CultureInfo.InvariantCulture))
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            const string userPath = "/admin/realms/waslabid/users/";
            if (request.Method == HttpMethod.Get && path.StartsWith(userPath, StringComparison.Ordinal) && !path[userPath.Length..].Contains('/', StringComparison.Ordinal))
            {
                var id = path[userPath.Length..];
                return AccountAnswers.TryGetValue(id, out var answer)
                    ? answer is "true" or "false"
                        ? Json($$"""{"id":"{{id}}","username":"{{id}}","enabled":{{answer}}}""")
                        : new HttpResponseMessage((HttpStatusCode)int.Parse(answer, System.Globalization.CultureInfo.InvariantCulture))
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path.EndsWith("/roles/vendor/users", StringComparison.Ordinal))
            {
                return Page(VendorUsers, request.RequestUri);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Page(List<string> ids, Uri uri)
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var first = int.Parse(query["first"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            var max = int.Parse(query["max"] ?? "100", System.Globalization.CultureInfo.InvariantCulture);
            return Json(System.Text.Json.JsonSerializer.Serialize(ids.Skip(first).Take(max).Select(id => new { id })));
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
