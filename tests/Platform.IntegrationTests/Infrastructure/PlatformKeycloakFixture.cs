using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;
using OtpNet;
using Testcontainers.Keycloak;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// Keycloak 26.3 with both realms (plan task 6): the tenant realm from the repository as it is, and the platform realm
/// from the repository with test-only additions written to a temporary file. The additions are an OTP credential with a
/// known secret for <c>platform.admin</c> (its CONFIGURE_TOTP required action removed, since the credential exists), a
/// second platform admin with a password only, a third user the brute-force test locks out, a tests-only password-grant
/// client, and a public copy of <c>waslabid-platform-web</c> without the PAR requirement so a test can open the realm's
/// authorization endpoint directly. None of them is in the repository file: a password grant on the platform realm would
/// skip the OTP form. The realm's own settings (brute force, password policy, flows, acr) are kept as they are.
/// </summary>
public sealed class PlatformKeycloakFixture : IAsyncLifetime
{
    public const string WebClientSecret = "test-client-secret";
    public const string PlatformClientSecret = "test-platform-client-secret";
    public const string UserPassword = "Test-Passw0rd-1";
    public const string OtpAdmin = "platform.admin";
    public const string PasswordOnlyAdmin = "platform.password-only";
    public const string LockoutUser = "platform.lockout";

    /// <summary>A second OTP admin with the same secret, so a test can sign in while another uses the same code.</summary>
    public const string SignOutAdmin = "platform.sign-out";

    /// <summary>Public copy of <c>waslabid-platform-web</c> (same attributes, mappers and scopes) without PAR or a secret.</summary>
    public const string ProbeClientId = "waslabid-platform-probe";

    /// <summary>The probe client without <c>minimum.acr.value</c>: the control that shows what the attribute changes.</summary>
    public const string NoMinimumAcrProbeClientId = "waslabid-platform-probe-no-min-acr";

    /// <summary>Where the probe client's login hands back; nothing listens there, the browser helper stops at it.</summary>
    public const string ProbeRedirectUri = "https://platform.localhost/signin-platform";
    private const string TestsClientId = "waslabid-platform-tests";

    // Keycloak's TOTP key is the UTF-8 bytes of the stored secret value, not its base32 decoding.
    private const string OtpSecret = "waslabid-test-otp-secret-0001";

    private readonly string _variantDirectory = Path.Combine(Path.GetTempPath(), $"waslabid-platform-realm-{Guid.NewGuid():N}");
    private KeycloakContainer? _container;

    private KeycloakContainer Container => _container ?? throw new InvalidOperationException("The fixture has not started.");

    public string BaseAddress => Container.GetBaseAddress();

    /// <summary>Issuer of the tenant realm, as the host's <c>Oidc:Authority</c> expects it.</summary>
    public string TenantAuthority => RealmUrl("waslabid");

    /// <summary>Issuer of the platform realm, as the host's <c>PlatformOidc:Authority</c> expects it.</summary>
    public string PlatformAuthority => RealmUrl("waslabid-platform");

    public OidcSettings OidcSettings => new(TenantAuthority, WebClientSecret, PlatformAuthority, PlatformClientSecret);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_variantDirectory);
        var variant = Path.Combine(_variantDirectory, "waslabid-platform-realm.json");
        await File.WriteAllTextAsync(variant, BuildTestVariant(await File.ReadAllTextAsync(RepoPaths.KeycloakPlatformRealm)));

        _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.3")
            .WithRealm(RepoPaths.KeycloakRealm)
            .WithResourceMapping(new FileInfo(variant), "/opt/keycloak/data/import/")
            .WithEnvironment("KC_FEATURES", "organization")
            .WithEnvironment("WASLABID_WEB_CLIENT_SECRET", WebClientSecret)
            .WithEnvironment("WASLABID_PLATFORM_CLIENT_SECRET", PlatformClientSecret)
            .WithEnvironment("WASLABID_DEV_USER_PASSWORD", UserPassword)
            .Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        Directory.Delete(_variantDirectory, recursive: true);
    }

    /// <summary>The current TOTP code for <see cref="OtpAdmin"/> and <see cref="SignOutAdmin"/>.</summary>
    public static string CurrentOtp() => new Totp(System.Text.Encoding.UTF8.GetBytes(OtpSecret)).ComputeTotp();

    /// <summary>
    /// Signs in to the platform realm through the tests-only password grant and returns the id token. Keycloak applies
    /// level-of-authentication conditions only in browser flows, so this token never carries acr 2.
    /// </summary>
    public async Task<string> PasswordGrantIdTokenAsync(string username, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = TestsClientId,
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = UserPassword,
            ["scope"] = "openid",
        });
        using var response = await http.PostAsync(new Uri($"{PlatformAuthority}/protocol/openid-connect/token"), form, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("id_token").GetString()!;
    }

    /// <summary>
    /// A plain authorization request for the probe client with PKCE and no <c>acr_values</c>, so only the client's
    /// <c>minimum.acr.value</c> can ask for the second factor. The code is never exchanged.
    /// </summary>
    public Uri ProbeAuthorizationUrl(string clientId = ProbeClientId)
    {
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes("probe-verifier-" + Guid.NewGuid().ToString("N"))));
        var query = string.Join('&', new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["redirect_uri"] = ProbeRedirectUri,
            ["state"] = Guid.NewGuid().ToString("N"),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new Uri($"{PlatformAuthority}/protocol/openid-connect/auth?{query}");
    }

    private string RealmUrl(string realm) => new Uri(new Uri(BaseAddress), $"realms/{realm}").ToString().TrimEnd('/');

    private static string BuildTestVariant(string repositoryRealm)
    {
        var realm = JsonNode.Parse(repositoryRealm)!.AsObject();
        var users = realm["users"]!.AsArray();
        var admin = users.Single(u => (string?)u!["username"] == OtpAdmin)!.AsObject();
        admin.Remove("requiredActions");
        admin["credentials"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "otp",
            ["userLabel"] = "integration tests",
            ["secretData"] = JsonSerializer.Serialize(new { value = OtpSecret }),
            ["credentialData"] = JsonSerializer.Serialize(new { subType = "totp", digits = 6, period = 30, algorithm = "HmacSHA1", counter = 0 }),
        });

        // Keycloak refuses a code already used on the same credential, so this admin has a credential of its own.
        var signOutAdmin = admin.DeepClone().AsObject();
        signOutAdmin["username"] = SignOutAdmin;
        signOutAdmin["email"] = "sign-out@waslabid.test";
        users.Add(signOutAdmin);

        users.Add(new JsonObject
        {
            ["username"] = PasswordOnlyAdmin,
            ["enabled"] = true,
            ["email"] = "password-only@waslabid.test",
            ["emailVerified"] = true,
            ["firstName"] = "Password",
            ["lastName"] = "Only",
            ["realmRoles"] = new JsonArray("platform-admin"),
            ["credentials"] = new JsonArray(new JsonObject
            {
                ["type"] = "password",
                ["value"] = "${WASLABID_DEV_USER_PASSWORD}",
                ["temporary"] = false,
            }),
        });

        users.Add(new JsonObject
        {
            ["username"] = LockoutUser,
            ["enabled"] = true,
            ["email"] = "lockout@waslabid.test",
            ["emailVerified"] = true,
            ["firstName"] = "Lockout",
            ["lastName"] = "Probe",
            ["realmRoles"] = new JsonArray("platform-admin"),
            ["credentials"] = new JsonArray(new JsonObject
            {
                ["type"] = "password",
                ["value"] = "${WASLABID_DEV_USER_PASSWORD}",
                ["temporary"] = false,
            }),
        });

        var webClient = realm["clients"]!.AsArray().Single(c => (string?)c!["clientId"] == "waslabid-platform-web")!;
        var probeClient = webClient.DeepClone().AsObject();
        probeClient["clientId"] = ProbeClientId;
        probeClient["name"] = "Automated tests only; the web client without PAR; never in the repository realm";
        probeClient["publicClient"] = true;
        probeClient.Remove("secret");
        probeClient["attributes"]!.AsObject().Remove("require.pushed.authorization.requests");
        var noMinimumAcr = probeClient.DeepClone().AsObject();
        noMinimumAcr["clientId"] = NoMinimumAcrProbeClientId;
        noMinimumAcr["attributes"]!.AsObject().Remove("minimum.acr.value");
        realm["clients"]!.AsArray().Add(probeClient);
        realm["clients"]!.AsArray().Add(noMinimumAcr);

        var testsClient = new JsonObject
        {
            ["clientId"] = TestsClientId,
            ["name"] = "Automated tests only; password grant; never in the repository realm",
            ["enabled"] = true,
            ["publicClient"] = true,
            ["standardFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = true,
            ["protocolMappers"] = webClient["protocolMappers"]!.DeepClone(),
            ["defaultClientScopes"] = webClient["defaultClientScopes"]!.DeepClone(),
        };
        realm["clients"]!.AsArray().Add(testsClient);
        return realm.ToJsonString();
    }
}
