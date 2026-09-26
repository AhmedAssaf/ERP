namespace Platform.Modules.Identity.Keycloak;

/// <summary>
/// The Keycloak Admin API as the web host reads it (section <c>KeycloakAdmin</c>, plan task 9, spec D-4). The client
/// secret comes from user secrets or the environment only (N-10). Without <see cref="BaseUrl"/> and
/// <see cref="ClientSecret"/> the client is not configured: member counts are unknown and invitations are refused.
/// </summary>
internal sealed class KeycloakAdminOptions
{
    public const string Section = "KeycloakAdmin";

    /// <summary>Keycloak's root URL, without the realm (Development: <c>http://localhost:8080</c>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The tenant realm.</summary>
    public string Realm { get; set; } = "waslabid";

    /// <summary>The confidential service-account client that calls the Admin API.</summary>
    public string ClientId { get; set; } = "waslabid-admin-api";

    public string? ClientSecret { get; set; }

    /// <summary>The client the invitation link belongs to; the redirect must be one of its registered redirect URIs.</summary>
    public string InvitationClientId { get; set; } = "waslabid-web";

    /// <summary>
    /// The tenant's home URL with <c>{slug}</c> for the tenant (Development: <c>https://{slug}.localhost:8443/</c>). The
    /// invitation link returns there, so the result must be registered exactly on <see cref="InvitationClientId"/>.
    /// </summary>
    public string? TenantUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>The tenant's home URL for <paramref name="slug"/>.</summary>
    public Uri TenantHome(string slug)
    {
        if (string.IsNullOrWhiteSpace(TenantUrl) || !TenantUrl.Contains("{slug}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Setting 'KeycloakAdmin:TenantUrl' must be an absolute URL containing {slug}.");
        }

        return new Uri(TenantUrl.Replace("{slug}", slug, StringComparison.Ordinal), UriKind.Absolute);
    }
}
