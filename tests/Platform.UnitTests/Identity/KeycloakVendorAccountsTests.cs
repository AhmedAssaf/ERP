using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Keycloak;

namespace Platform.UnitTests.Identity;

/// <summary>
/// W-33 review, major 2: taking back a vendor's access is best effort (a failing step is logged and the other still runs),
/// but it never reports a failed removal as done, so an upheld dispute whose squatter kept the realm role stays visible in
/// the console for a retry.
/// </summary>
public sealed class KeycloakVendorAccountsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_role_removal_that_keycloak_refuses_is_reported_as_not_done()
    {
        var handler = new Handler(removeRole: HttpStatusCode.InternalServerError);
        var accounts = Accounts(handler);

        var undone = await accounts.RevokeAsync(new VendorAccessGrant("u1", "acme", RoleAdded: true, OrganizationAdded: true), Ct);

        undone.ShouldBeFalse();
        // The organization step still ran.
        handler.Requests.ShouldContain(r => r.StartsWith("DELETE /admin/realms/waslabid/organizations/org-1/members/u1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_removal_keycloak_accepts_is_reported_as_done()
    {
        var accounts = Accounts(new Handler(removeRole: HttpStatusCode.NoContent));

        (await accounts.RevokeAsync(new VendorAccessGrant("u1", string.Empty, RoleAdded: true, OrganizationAdded: false), Ct)).ShouldBeTrue();
    }

    private static KeycloakVendorAccounts Accounts(Handler handler) =>
        new(
            new KeycloakAdminClient(
                new HttpClient(handler) { BaseAddress = new Uri("http://keycloak.test/") },
                new KeycloakAdminState(TimeProvider.System),
                Options.Create(new KeycloakAdminOptions { BaseUrl = "http://keycloak.test", ClientSecret = "secret" })),
            NullLogger<KeycloakVendorAccounts>.Instance);

    private sealed class Handler(HttpStatusCode removeRole) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"access_token":"token-1","expires_in":300,"token_type":"Bearer"}"""));
            }

            if (path.EndsWith("/organizations", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""[{"id":"org-1","alias":"acme","name":"Acme"}]"""));
            }

            if (path.EndsWith("/roles/vendor", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"id":"role-1","name":"vendor"}"""));
            }

            if (request.Method == HttpMethod.Delete && path.EndsWith("/role-mappings/realm", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(removeRole));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
