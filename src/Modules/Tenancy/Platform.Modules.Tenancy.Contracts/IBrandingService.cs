using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Tenancy.Contracts;

/// <summary>A saved portal name and colour: the stored branding and the colour the admin asked for (spec D-11).</summary>
public sealed record BrandingSaved(TenantBranding Branding, string RequestedColor)
{
    /// <summary>True when the requested colour had under 4.5:1 contrast with white and a darker one was stored.</summary>
    public bool ColorAdjusted => !string.Equals(Branding.PrimaryColor, RequestedColor, StringComparison.Ordinal);
}

/// <summary>
/// The current tenant's branding (F-02 as narrowed, spec 4.3 and D-9 to D-11): portal name, primary colour and logo.
/// Every call works on the tenant of the current request or circuit and can change only that tenant. A save acts as the
/// request or circuit's acting user: <c>actorId</c> must be that user (anything else throws), and a user who is not an
/// active tenant admin of the tenant, or who is a vendor user, gets <see cref="BrandingErrors.NotAllowed"/>. Error codes
/// are the constants of <see cref="BrandingErrors"/>.
/// </summary>
public interface IBrandingService
{
    /// <summary>The branding the current request or circuit resolved with its tenant.</summary>
    Task<TenantBranding> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the portal name (trimmed, 1 to 100 characters, no control characters) and primary colour (<c>#RRGGBB</c>).
    /// A colour under 4.5:1 contrast with white is darkened until it passes and the darker value stored; the result says
    /// so. Audited as <c>tenancy.branding_changed</c> under <paramref name="actorId"/>.
    /// </summary>
    Task<Result<BrandingSaved>> SaveAsync(string portalName, string primaryColor, string actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the logo: PNG or JPEG by magic number (and <paramref name="contentType"/>, when given, one of the two), at
    /// most 512 KB and 4096 px on a side; re-encoded to PNG at most 1024 px on the long side and stored under the tenant's
    /// prefix by its SHA-256. Audited as <c>tenancy.branding_changed</c> under <paramref name="actorId"/>.
    /// </summary>
    Task<Result<TenantBranding>> SaveLogoAsync(Stream content, string? contentType, string actorId, CancellationToken cancellationToken = default);

    /// <summary>The current tenant's logo with this SHA-256 (64 lower-case hex digits) as PNG, or null.</summary>
    Task<Stream?> OpenLogoAsync(string hash, CancellationToken cancellationToken = default);
}

/// <summary>The error codes of <see cref="IBrandingService"/>.</summary>
public static class BrandingErrors
{
    public const string InvalidPortalName = "tenancy.invalid_portal_name";
    public const string InvalidColor = "tenancy.invalid_color";
    public const string LogoTooLarge = "tenancy.logo_too_large";
    public const string LogoNotImage = "tenancy.logo_not_image";
    public const string LogoTooManyPixels = "tenancy.logo_too_many_pixels";
    public const string LogoUnreadable = "tenancy.logo_unreadable";

    /// <summary>The acting user is not an active tenant admin of this tenant, or is a vendor user (tenancy migration 0008).</summary>
    public const string NotAllowed = "tenancy.branding_not_allowed";
}
