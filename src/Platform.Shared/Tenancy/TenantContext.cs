namespace Platform.Shared.Tenancy;

public sealed record TenantBranding(string PortalName, string PrimaryColor, string? LogoUrl);

/// <summary>The tenant a request or circuit belongs to, resolved from the host name (spec section 2.1).</summary>
public sealed record TenantContext(
    Guid TenantId,
    string Slug,
    string KeycloakOrgAlias,
    string DefaultCulture,
    TenantBranding Branding);

public interface ITenantAccessor
{
    TenantContext? Current { get; }
}

/// <summary>Scoped holder set once per request or circuit. Only infrastructure code sets it.</summary>
public sealed class TenantAccessor : ITenantAccessor
{
    public TenantContext? Current { get; set; }
}
