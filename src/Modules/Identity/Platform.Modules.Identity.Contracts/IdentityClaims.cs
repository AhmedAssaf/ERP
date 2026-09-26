namespace Platform.Modules.Identity.Contracts;

/// <summary>Claim names as Keycloak issues them; inbound claim mapping is off, so they arrive unchanged.</summary>
public static class IdentityClaims
{
    public const string Subject = "sub";
    public const string Username = "preferred_username";
    public const string Locale = "locale";
    public const string Organization = "organization";

    /// <summary>Authentication context class: the level of authentication Keycloak achieved for the login (D-2).</summary>
    public const string Acr = "acr";

    /// <summary>Realm roles, as the platform realm's client mapper emits them in the id token (one claim per role).</summary>
    public const string Roles = "roles";

    public const string Email = "email";

    /// <summary>"true" when Keycloak verified the address; member rows are bound by email only then.</summary>
    public const string EmailVerified = "email_verified";

    /// <summary>
    /// A tenant role from <c>identity.members</c> (F-07). The members claims transformation adds these on its own identity
    /// for the host tenant only; the tenant policies read them from that identity alone.
    /// </summary>
    public const string Role = "role";
}
