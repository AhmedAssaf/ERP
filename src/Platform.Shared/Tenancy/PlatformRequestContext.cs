namespace Platform.Shared.Tenancy;

/// <summary>
/// Whether the current request or circuit belongs to the platform console (spec 3.1). Platform-wide reads that cross
/// tenants (<c>ITenantCatalog</c>) refuse unless it says so, as a guard in code on top of the PlatformAdmin policy: the
/// database cannot tell a console request from a tenant request, because both connect as <c>erp_app</c>.
/// </summary>
public interface IPlatformRequestContext
{
    bool IsPlatform { get; }
}

/// <summary>
/// Scoped holder, false until host infrastructure marks the scope: the web host's platform-host middleware for a
/// request and its circuit handler for a circuit. Nothing else calls <see cref="MarkPlatform"/>; the worker and the
/// migrator never do, so any cross-tenant read there fails closed.
/// </summary>
public sealed class PlatformRequestContext : IPlatformRequestContext
{
    public bool IsPlatform { get; private set; }

    public void MarkPlatform() => IsPlatform = true;
}
