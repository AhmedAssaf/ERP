using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Platform.Modules.Identity.Contracts;

namespace Platform.Modules.Identity.Keycloak;

/// <summary>A Keycloak user as the Admin API returns it; only the fields the staff service reads.</summary>
internal sealed record KeycloakUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>A user to create: the email is also the username; the locale is Keycloak's (<c>ar</c> or <c>en</c>).</summary>
internal sealed record NewKeycloakUser(string Email, string FirstName, string LastName, string Locale);

/// <summary>
/// Keycloak answered an Admin API call with an unexpected status. The message names the call, never a secret.
/// <see cref="DuringUserCreation"/> is true only for the create-user request itself, the one call whose body carries the
/// person's name, so a 400 from an earlier step of that same call (fetching the service account's token, say) is never
/// mistaken for the user profile refusing the name.
/// </summary>
internal sealed class KeycloakAdminException(string message, HttpStatusCode? status = null, bool duringUserCreation = false) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;

    public bool DuringUserCreation { get; } = duringUserCreation;
}

/// <summary>
/// What the Admin API client keeps between calls (singleton): the service account's access token until shortly before it
/// expires, and organization ids by alias (Keycloak generates them on import and they never change).
/// </summary>
internal sealed class KeycloakAdminState(TimeProvider clock) : IDisposable
{
    /// <summary>A token is renewed once less than this remains, so a call never starts with a token about to expire.</summary>
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private (string Value, DateTimeOffset ExpiresAt)? _token;

    public ConcurrentDictionary<string, string> OrganizationIds { get; } = new(StringComparer.Ordinal);

    public async Task<string> TokenAsync(HttpClient http, KeycloakAdminOptions options, CancellationToken cancellationToken)
    {
        if (Fresh() is { } cached)
        {
            return cached;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh() is { } again)
            {
                return again;
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret ?? string.Empty,
            });
            using var response = await http.PostAsync(
                new Uri($"realms/{Uri.EscapeDataString(options.Realm)}/protocol/openid-connect/token", UriKind.Relative), form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new KeycloakAdminException($"Keycloak refused the service account's token request ({(int)response.StatusCode}).", response.StatusCode);
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new KeycloakAdminException("Keycloak's token response was empty.");
            _token = (token.AccessToken, clock.GetUtcNow().AddSeconds(token.ExpiresIn));
            return token.AccessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    public void ForgetToken() => _token = null;

    public void Dispose() => _tokenGate.Dispose();

    private string? Fresh() =>
        _token is { } token && clock.GetUtcNow() < token.ExpiresAt - RenewBefore ? token.Value : null;

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>
/// The Keycloak Admin API for the tenant realm (plan task 9, spec D-4), as the confidential service-account client
/// <c>waslabid-admin-api</c>. A typed <see cref="HttpClient"/>; the token and organization ids live in
/// <see cref="KeycloakAdminState"/>. Keycloak 26.3 serves every <c>/organizations</c> endpoint, reads included, only to
/// the realm-management role <c>manage-realm</c>; the users endpoints need <c>manage-users</c>, <c>view-users</c> and
/// <c>query-users</c>; reading a realm role by name needs <c>view-realm</c> (held through <c>manage-realm</c>) and mapping
/// it to a user <c>manage-users</c> (infra/compose/keycloak/import/README.md).
/// </summary>
internal sealed class KeycloakAdminClient(HttpClient http, KeycloakAdminState state, IOptions<KeycloakAdminOptions> options)
{
    /// <summary>How long the invitation link stays valid (F-06 as narrowed, spec 4.2).</summary>
    public static readonly TimeSpan InvitationLifespan = TimeSpan.FromHours(72);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private KeycloakAdminOptions Options => options.Value;

    private string Realm => $"admin/realms/{Uri.EscapeDataString(Options.Realm)}";

    /// <summary>The user whose email is <paramref name="email"/> (Keycloak compares it case-insensitively), or null.</summary>
    public async Task<KeycloakUser?> FindUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var users = await GetAsync<List<KeycloakUser>>($"{Realm}/users?email={Uri.EscapeDataString(email)}&exact=true", cancellationToken);
        return users?.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Creates an enabled user with an unverified email and returns its id (the <c>sub</c> of its tokens). Keycloak answers
    /// 400 when its user profile refuses a value (a name, say); the exception then carries that status.
    /// </summary>
    public async Task<string> CreateUserAsync(NewKeycloakUser user, CancellationToken cancellationToken)
    {
        var body = new
        {
            username = user.Email,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            enabled = true,
            emailVerified = false,
            attributes = new Dictionary<string, string[]> { ["locale"] = [user.Locale] },
        };
        using var response = await SendAsync(HttpMethod.Post, $"{Realm}/users", body, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created || response.Headers.Location is not { } location)
        {
            throw new KeycloakAdminException($"Keycloak did not create the user ({(int)response.StatusCode}).", response.StatusCode, duringUserCreation: true);
        }

        return location.Segments[^1].TrimEnd('/');
    }

    /// <summary>
    /// Adds the user to the organization with <paramref name="alias"/>. True when this call added them; false when they
    /// were already a member (Keycloak answers 409), in which case nothing changes.
    /// </summary>
    public async Task<bool> AddToOrganizationAsync(string alias, string userId, CancellationToken cancellationToken)
    {
        var organizationId = await OrganizationIdAsync(alias, cancellationToken)
            ?? throw new KeycloakAdminException($"Keycloak has no organization '{alias}'.");
        using var response = await SendAsync(HttpMethod.Post, $"{Realm}/organizations/{organizationId}/members", userId, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.Created or HttpStatusCode.NoContent => true,
            HttpStatusCode.Conflict => false,
            _ => throw new KeycloakAdminException(
                $"Keycloak did not add the user to organization '{alias}' ({(int)response.StatusCode}).", response.StatusCode),
        };
    }

    /// <summary>Removes the user from the organization with <paramref name="alias"/>; a user not in it is left as is.</summary>
    public async Task RemoveFromOrganizationAsync(string alias, string userId, CancellationToken cancellationToken)
    {
        var organizationId = await OrganizationIdAsync(alias, cancellationToken)
            ?? throw new KeycloakAdminException($"Keycloak has no organization '{alias}'.");
        using var response = await SendAsync(
            HttpMethod.Delete, $"{Realm}/organizations/{organizationId}/members/{Uri.EscapeDataString(userId)}", null, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.NotFound))
        {
            throw new KeycloakAdminException(
                $"Keycloak did not remove the user from organization '{alias}' ({(int)response.StatusCode}).", response.StatusCode);
        }
    }

    /// <summary>The types of the user's credentials (<c>password</c>, <c>otp</c>, ...).</summary>
    public async Task<IReadOnlySet<string>> CredentialTypesAsync(string userId, CancellationToken cancellationToken)
    {
        var credentials = await GetAsync<List<CredentialResponse>>($"{Realm}/users/{Uri.EscapeDataString(userId)}/credentials", cancellationToken);
        return (credentials ?? []).Select(c => c.Type).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Has Keycloak email the user a link that runs <paramref name="actions"/> (required actions such as
    /// <c>UPDATE_PASSWORD</c> and <c>CONFIGURE_TOTP</c>) and then offers to return to <paramref name="redirectUri"/>, which
    /// Keycloak accepts only when it is a registered redirect URI of the invitation client. The link lives
    /// <see cref="InvitationLifespan"/>.
    /// </summary>
    public async Task SendInvitationEmailAsync(
        string userId, IReadOnlyCollection<string> actions, Uri redirectUri, CancellationToken cancellationToken)
    {
        var query = string.Join('&', new Dictionary<string, string>
        {
            ["client_id"] = Options.InvitationClientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["lifespan"] = ((int)InvitationLifespan.TotalSeconds).ToString(CultureInfo.InvariantCulture),
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        using var response = await SendAsync(
            HttpMethod.Put, $"{Realm}/users/{Uri.EscapeDataString(userId)}/execute-actions-email?{query}", actions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new KeycloakAdminException($"Keycloak did not send the invitation email ({(int)response.StatusCode}).", response.StatusCode);
        }
    }

    /// <summary>The names of the realm roles mapped directly to the user.</summary>
    public async Task<IReadOnlySet<string>> RealmRolesAsync(string userId, CancellationToken cancellationToken)
    {
        var roles = await GetAsync<List<RoleResponse>>($"{Realm}/users/{Uri.EscapeDataString(userId)}/role-mappings/realm", cancellationToken);
        return (roles ?? []).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Maps the realm role <paramref name="role"/> to the user. True when this call added it; false when the user already
    /// held it, in which case nothing changes.
    /// </summary>
    public async Task<bool> AssignRealmRoleAsync(string userId, string role, CancellationToken cancellationToken)
    {
        if ((await RealmRolesAsync(userId, cancellationToken)).Contains(role))
        {
            return false;
        }

        var representation = await RealmRoleAsync(role, cancellationToken);
        using var response = await SendAsync(
            HttpMethod.Post, $"{Realm}/users/{Uri.EscapeDataString(userId)}/role-mappings/realm", new[] { representation }, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            throw new KeycloakAdminException($"Keycloak did not grant realm role '{role}' ({(int)response.StatusCode}).", response.StatusCode);
        }

        return true;
    }

    /// <summary>Removes the realm role <paramref name="role"/> from the user; a user without it is left as is.</summary>
    public async Task RemoveRealmRoleAsync(string userId, string role, CancellationToken cancellationToken)
    {
        var representation = await RealmRoleAsync(role, cancellationToken);
        using var response = await SendAsync(
            HttpMethod.Delete, $"{Realm}/users/{Uri.EscapeDataString(userId)}/role-mappings/realm", new[] { representation }, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.NotFound))
        {
            throw new KeycloakAdminException($"Keycloak did not remove realm role '{role}' ({(int)response.StatusCode}).", response.StatusCode);
        }
    }

    /// <summary>The aliases of every organization the user is a member of.</summary>
    public async Task<IReadOnlyList<string>> OrganizationAliasesOfAsync(string userId, CancellationToken cancellationToken)
    {
        var organizations = await GetAsync<List<OrganizationResponse>>(
            $"{Realm}/organizations/members/{Uri.EscapeDataString(userId)}/organizations?briefRepresentation=true", cancellationToken);
        return [.. (organizations ?? []).Select(o => o.Alias)];
    }

    /// <summary>
    /// Whether the user is an enabled member of the organization with <paramref name="alias"/> (W-21), in one call to the
    /// organization's member endpoint: 200 is a member (disabled when its account is), 404 is not a member or no such user.
    /// An organization that does not exist has no members. Any other status throws <see cref="KeycloakAdminException"/>.
    /// </summary>
    public async Task<OrganizationMembership> MembershipAsync(string alias, string userId, CancellationToken cancellationToken)
    {
        var organizationId = await OrganizationIdAsync(alias, cancellationToken);
        if (organizationId is null)
        {
            return OrganizationMembership.NotMember;
        }

        using var response = await SendAsync(
            HttpMethod.Get, $"{Realm}/organizations/{organizationId}/members/{Uri.EscapeDataString(userId)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return OrganizationMembership.NotMember;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new KeycloakAdminException($"Keycloak refused the organization member check ({(int)response.StatusCode}).", response.StatusCode);
        }

        var member = await response.Content.ReadFromJsonAsync<MemberResponse>(Json, cancellationToken)
            ?? throw new KeycloakAdminException("Keycloak's organization member response was empty.");
        return member.Enabled ? OrganizationMembership.Member : OrganizationMembership.Disabled;
    }

    /// <summary>
    /// The number of the tenant's users in the organization with <paramref name="alias"/> (F-54): its members less those
    /// holding the realm role <c>vendor</c>, who join a tenant's organization as vendors (V-3), not as its users. Null when
    /// there is no such organization. Without any vendor in the realm this is Keycloak's member count; otherwise the
    /// member list and the role's users are read a page at a time and compared by id.
    /// </summary>
    public async Task<int?> CountOrganizationUsersAsync(string alias, CancellationToken cancellationToken)
    {
        var organizationId = await OrganizationIdAsync(alias, cancellationToken);
        if (organizationId is null)
        {
            return null;
        }

        var vendors = await IdsAsync($"{Realm}/roles/{Uri.EscapeDataString(IdentityClaims.VendorRealmRole)}/users?briefRepresentation=true", cancellationToken);
        if (vendors.Count == 0)
        {
            return await GetAsync<int>($"{Realm}/organizations/{organizationId}/members/count", cancellationToken);
        }

        var members = await IdsAsync($"{Realm}/organizations/{organizationId}/members?briefRepresentation=true", cancellationToken);
        return members.Count(id => !vendors.Contains(id));
    }

    // Every id of a paged Admin API list (first/max), read until a page comes back short.
    private async Task<HashSet<string>> IdsAsync(string path, CancellationToken cancellationToken)
    {
        const int page = 100;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var first = 0; ; first += page)
        {
            var batch = await GetAsync<List<IdResponse>>($"{path}&first={first}&max={page}", cancellationToken) ?? [];
            ids.UnionWith(batch.Select(u => u.Id));
            if (batch.Count < page)
            {
                return ids;
            }
        }
    }

    // Keycloak's organization search matches names and domains, not aliases, so the list is read a page at a time.
    private async Task<string?> OrganizationIdAsync(string alias, CancellationToken cancellationToken)
    {
        if (state.OrganizationIds.TryGetValue(alias, out var known))
        {
            return known;
        }

        const int page = 100;
        for (var first = 0; ; first += page)
        {
            var organizations = await GetAsync<List<OrganizationResponse>>(
                $"{Realm}/organizations?briefRepresentation=true&first={first}&max={page}", cancellationToken) ?? [];
            foreach (var organization in organizations)
            {
                state.OrganizationIds[organization.Alias] = organization.Id;
            }

            if (state.OrganizationIds.TryGetValue(alias, out var found))
            {
                return found;
            }

            if (organizations.Count < page)
            {
                return null;
            }
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new KeycloakAdminException($"Keycloak refused GET {PathOnly(path)} ({(int)response.StatusCode}).", response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
    }

    // One retry with a new token when Keycloak says the cached one is no longer valid (a restart, say).
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (!Options.IsConfigured)
        {
            throw new InvalidOperationException("The Keycloak Admin API is not configured (settings KeycloakAdmin:BaseUrl and KeycloakAdmin:ClientSecret).");
        }

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await state.TokenAsync(http, Options, cancellationToken));
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            }

            var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
            {
                return response;
            }

            response.Dispose();
            state.ForgetToken();
        }
    }

    private static string PathOnly(string path) => path.Split('?')[0];

    private async Task<RoleResponse> RealmRoleAsync(string role, CancellationToken cancellationToken) =>
        await GetAsync<RoleResponse>($"{Realm}/roles/{Uri.EscapeDataString(role)}", cancellationToken)
        ?? throw new KeycloakAdminException($"Keycloak has no realm role '{role}'.");

    private sealed record IdResponse([property: JsonPropertyName("id")] string Id);

    private sealed record MemberResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("enabled")] bool Enabled);

    private sealed record CredentialResponse([property: JsonPropertyName("type")] string Type);

    private sealed record RoleResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record OrganizationResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("alias")] string Alias);
}
