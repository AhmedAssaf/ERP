using System.Text.Json;
using Testcontainers.Keycloak;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>Keycloak 26.3 with the repository's realm file, the same image as infra/compose.</summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    /// <summary>Secret of the waslabid-web client in this test container only.</summary>
    public const string WebClientSecret = "test-client-secret";

    private const string UserPassword = "Test-Passw0rd-1";

    private readonly KeycloakContainer _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.3")
        .WithRealm(RepoPaths.KeycloakRealm)
        .WithEnvironment("KC_FEATURES", "organization")
        .WithEnvironment("WASLABID_WEB_CLIENT_SECRET", WebClientSecret)
        .WithEnvironment("WASLABID_DEV_USER_PASSWORD", UserPassword)
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>The waslabid realm's issuer URL, as the host's <c>Oidc:Authority</c> expects it.</summary>
    public string Authority => new Uri(new Uri(_container.GetBaseAddress()), "realms/waslabid").ToString().TrimEnd('/');

    /// <summary>Signs in through the tests-only client and returns the id token.</summary>
    public async Task<string> SignInAsync(string username, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var tokenEndpoint = new Uri(new Uri(_container.GetBaseAddress()), "realms/waslabid/protocol/openid-connect/token");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "waslabid-tests",
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = UserPassword,
            ["scope"] = "openid organization",
        });
        using var response = await http.PostAsync(tokenEndpoint, form, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("id_token").GetString()!;
    }
}
