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

/// <summary>
/// Scoped holder set once per request or circuit. Only host infrastructure (middleware, circuit handler,
/// migrator, tests) may call <see cref="Set"/>; application code should depend on <see cref="ITenantAccessor"/>
/// and only read <see cref="Current"/>.
/// </summary>
public sealed class TenantAccessor : ITenantAccessor
{
    public TenantContext? Current { get; private set; }

    /// <summary>Sets the tenant once. Setting the same tenant again is a no-op; setting a different tenant throws.</summary>
    public void Set(TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (Current is not null && Current != tenant)
        {
            throw new InvalidOperationException(
                $"The tenant is already set to '{Current.Slug}'; it cannot be changed to '{tenant.Slug}' within the same request or circuit.");
        }

        Current = tenant;
    }
}
