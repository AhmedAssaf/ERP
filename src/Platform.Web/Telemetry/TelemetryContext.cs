using System.Diagnostics;
using Platform.Shared.Telemetry;
using Platform.Shared.Tenancy;

namespace Platform.Web.Telemetry;

/// <summary>
/// The context of O-9 as span tags and log scope entries: pseudonymous ids only (the tenant's id and slug, the user's Keycloak
/// <c>sub</c>, the vendor company's id), never an email, a name or a CR number. A log scope given these pairs becomes event
/// properties under the same keys (the Serilog provider reads scopes of key and value pairs).
/// </summary>
internal static class TelemetryContext
{
    public static KeyValuePair<string, object?>[] Tenant(TenantContext tenant) =>
    [
        new(TelemetryNames.Attributes.TenantId, tenant.TenantId.ToString()),
        new(TelemetryNames.Attributes.TenantSlug, tenant.Slug),
    ];

    public static KeyValuePair<string, object?>[] User(string userId) => [new(TelemetryNames.Attributes.UserId, userId)];

    public static KeyValuePair<string, object?>[] Vendor(VendorContext vendor) =>
        [new(TelemetryNames.Attributes.VendorCompanyId, vendor.CompanyId.ToString())];

    /// <summary>Every part of the context that is set: a circuit event's scope.</summary>
    public static KeyValuePair<string, object?>[] Of(TenantContext? tenant, string? userId, VendorContext? vendor) =>
    [
        .. tenant is null ? [] : Tenant(tenant),
        .. string.IsNullOrEmpty(userId) ? [] : User(userId),
        .. vendor is null ? [] : Vendor(vendor),
    ];

    public static void Tag(Activity? span, KeyValuePair<string, object?>[] tags)
    {
        if (span is null)
        {
            return;
        }

        foreach (var (key, value) in tags)
        {
            span.SetTag(key, value);
        }
    }
}
