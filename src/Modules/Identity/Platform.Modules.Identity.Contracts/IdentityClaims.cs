namespace Platform.Modules.Identity.Contracts;

/// <summary>Claim names as Keycloak issues them; inbound claim mapping is off, so they arrive unchanged.</summary>
public static class IdentityClaims
{
    public const string Subject = "sub";
    public const string Username = "preferred_username";
    public const string Locale = "locale";
    public const string Organization = "organization";
}
