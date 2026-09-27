using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using OtpNet;
using Testcontainers.Keycloak;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// Keycloak 26.3 with the tenant realm (plan task 9), the same image as infra/compose, and Mailpit on a shared Docker
/// network under the alias <c>mailpit</c>, so the realm's SMTP setting (<c>mailpit:1025</c>) reaches it exactly as in
/// Compose. The realm is the repository file with test-only additions written to a temporary file: the development
/// admins lose their <c>CONFIGURE_TOTP</c> required action (the password grant of <c>waslabid-tests</c> refuses an
/// account that is not fully set up), and two acme users get an OTP credential with a known secret, so a test can drive
/// the browser flow's required OTP form. Everything else (brute force, flows, SMTP, clients) is kept as it is.
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    /// <summary>Secret of the waslabid-web client in this test container only.</summary>
    public const string WebClientSecret = "test-client-secret";

    /// <summary>Secret of the waslabid-admin-api service account in this test container only.</summary>
    public const string AdminApiSecret = "test-admin-api-secret";

    public const string UserPassword = "Test-Passw0rd-1";

    /// <summary>An acme user with a password and an OTP credential, for a login that succeeds.</summary>
    public const string OtpUser = "acme.otp";

    /// <summary>An acme user with its own OTP credential, which the lockout test locks.</summary>
    public const string LockoutUser = "acme.lockout";

    /// <summary>A user in no organization with a password and a TOTP credential: an account that needs no setup.</summary>
    public const string ReadyUser = "ready.person";

    /// <summary>A disabled user in no organization.</summary>
    public const string DisabledUser = "disabled.person";

    // Keycloak's TOTP key is the UTF-8 bytes of the stored secret value, not its base32 decoding.
    private const string OtpSecret = "waslabid-test-otp-secret-0002";

    private readonly string _variantDirectory = Path.Combine(Path.GetTempPath(), $"waslabid-realm-{Guid.NewGuid():N}");
    private readonly INetwork _network = new NetworkBuilder().Build();
    private IContainer? _mailpit;
    private KeycloakContainer? _keycloak;

    private KeycloakContainer Keycloak => _keycloak ?? throw new InvalidOperationException("The fixture has not started.");

    private IContainer Mailpit => _mailpit ?? throw new InvalidOperationException("The fixture has not started.");

    public string BaseAddress => Keycloak.GetBaseAddress();

    /// <summary>The waslabid realm's issuer URL, as the host's <c>Oidc:Authority</c> expects it.</summary>
    public string Authority => new Uri(new Uri(BaseAddress), "realms/waslabid").ToString().TrimEnd('/');

    /// <summary>Mailpit's HTTP API, where the mail Keycloak sent can be read.</summary>
    public Uri MailpitApi => new($"http://{Mailpit.Hostname}:{Mailpit.GetMappedPublicPort(8025)}");

    /// <summary>Mailpit's SMTP endpoint from the host, where the app's own emails (<c>Smtp:*</c>) go in tests.</summary>
    public (string Host, int Port) MailpitSmtp => (Mailpit.Hostname, Mailpit.GetMappedPublicPort(1025));

    /// <summary>The email address of a user this fixture adds (<see cref="ReadyUser"/>, <see cref="DisabledUser"/>).</summary>
    public static string EmailOf(string username) => $"{username}@acme.waslabid.test";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_variantDirectory);
        var variant = Path.Combine(_variantDirectory, "waslabid-realm.json");
        await File.WriteAllTextAsync(variant, BuildTestVariant(await File.ReadAllTextAsync(RepoPaths.KeycloakRealm)));

        await _network.CreateAsync();
        _mailpit = new ContainerBuilder("axllent/mailpit:v1.31.3@sha256:ed9b00c609e77e99c79b93f1178255ebc271868920f2c69a8d166bd5634ed10d")
            .WithNetwork(_network)
            .WithNetworkAliases("mailpit")
            .WithPortBinding(8025, assignRandomHostPort: true)
            .WithPortBinding(1025, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8025))
            .Build();
        _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:26.3")
            .WithNetwork(_network)
            .WithRealm(variant)
            .WithEnvironment("KC_FEATURES", "organization")
            .WithEnvironment("WASLABID_WEB_CLIENT_SECRET", WebClientSecret)
            .WithEnvironment("WASLABID_ADMIN_API_SECRET", AdminApiSecret)
            .WithEnvironment("WASLABID_DEV_USER_PASSWORD", UserPassword)
            .Build();
        await Task.WhenAll(_mailpit.StartAsync(), _keycloak.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (_keycloak is not null)
        {
            await _keycloak.DisposeAsync();
        }

        if (_mailpit is not null)
        {
            await _mailpit.DisposeAsync();
        }

        await _network.DisposeAsync();
        if (Directory.Exists(_variantDirectory))
        {
            Directory.Delete(_variantDirectory, recursive: true);
        }
    }

    /// <summary>The current TOTP code for <see cref="OtpUser"/> and <see cref="LockoutUser"/>.</summary>
    public static string CurrentOtp() => new Totp(System.Text.Encoding.UTF8.GetBytes(OtpSecret)).ComputeTotp();

    /// <summary>Signs in through the tests-only client and returns the id token.</summary>
    public async Task<string> SignInAsync(string username, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "waslabid-tests",
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = UserPassword,
            ["scope"] = "openid organization",
        });
        using var response = await http.PostAsync(new Uri($"{Authority}/protocol/openid-connect/token"), form, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("id_token").GetString()!;
    }

    /// <summary>
    /// Calls the waslabid realm's Admin API as the container's bootstrap admin (master realm), for assertions the app's
    /// own service account is not asked to make. <paramref name="path"/> is relative to <c>admin/realms/waslabid/</c>.
    /// </summary>
    public async Task<JsonElement> AdminGetAsync(string path, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, AdminUri(path));
        request.Headers.Authorization = new("Bearer", await BootstrapTokenAsync(http, cancellationToken));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body.RootElement.Clone();
    }

    /// <summary>
    /// Creates an enabled user in no organization, with <paramref name="email"/> as username and email, a password, and no
    /// second factor, as the container's bootstrap admin; returns its id.
    /// </summary>
    public async Task<string> CreateUserAsync(string email, string password, bool emailVerified, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, AdminUri("users"))
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                username = email,
                email,
                firstName = "Test",
                lastName = "Person",
                enabled = true,
                emailVerified,
                credentials = new[] { new { type = "password", value = password, temporary = false } },
            }),
        };
        request.Headers.Authorization = new("Bearer", await BootstrapTokenAsync(http, cancellationToken));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response.Headers.Location!.Segments[^1].TrimEnd('/');
    }

    private Uri AdminUri(string path) =>
        new(new Uri(BaseAddress), path.Length == 0 ? "admin/realms/waslabid" : $"admin/realms/waslabid/{path}");

    private async Task<string> BootstrapTokenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "admin-cli",
            ["grant_type"] = "password",
            ["username"] = KeycloakBuilder.DefaultUsername,
            ["password"] = KeycloakBuilder.DefaultPassword,
        });
        using var tokenResponse = await http.PostAsync(new Uri(new Uri(BaseAddress), "realms/master/protocol/openid-connect/token"), form, cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
        return token.RootElement.GetProperty("access_token").GetString()!;
    }

    private static string BuildTestVariant(string repositoryRealm)
    {
        var realm = JsonNode.Parse(repositoryRealm)!.AsObject();
        var users = realm["users"]!.AsArray();
        foreach (var admin in users.Where(u => ((string?)u!["username"])?.EndsWith(".admin", StringComparison.Ordinal) == true))
        {
            admin!.AsObject().Remove("requiredActions");
        }

        foreach (var username in new[] { OtpUser, LockoutUser, ReadyUser, DisabledUser })
        {
            users.Add(new JsonObject
            {
                ["username"] = username,
                ["enabled"] = username != DisabledUser,
                ["email"] = $"{username}@acme.waslabid.test",
                ["emailVerified"] = true,
                ["firstName"] = "Acme",
                ["lastName"] = username,
                ["credentials"] = new JsonArray(
                    new JsonObject { ["type"] = "password", ["value"] = "${WASLABID_DEV_USER_PASSWORD}", ["temporary"] = false },
                    new JsonObject
                    {
                        ["type"] = "otp",
                        ["userLabel"] = "integration tests",
                        ["secretData"] = JsonSerializer.Serialize(new { value = OtpSecret }),
                        ["credentialData"] = JsonSerializer.Serialize(new { subType = "totp", digits = 6, period = 30, algorithm = "HmacSHA1", counter = 0 }),
                    }),
            });
        }

        var acme = realm["organizations"]!.AsArray().Single(o => (string?)o!["alias"] == "acme")!;
        foreach (var username in new[] { OtpUser, LockoutUser })
        {
            acme["members"]!.AsArray().Add(new JsonObject { ["username"] = username, ["membershipType"] = "MANAGED" });
        }

        return realm.ToJsonString();
    }
}
