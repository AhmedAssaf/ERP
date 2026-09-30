using System.Security.Claims;
using Platform.Modules.Identity;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Telemetry;
using Platform.Shared.Tenancy;

namespace Platform.Web.Usage;

/// <summary>The kinds of user the usage metrics count (O-24, spec 6.2).</summary>
internal enum UsageKind
{
    Staff,
    Vendor,
    Platform,
}

/// <summary>
/// The one classifier of spec 6.2, used by the circuit registry (concurrent users) and the activity recorder (active
/// users). It reads the scope as the host has set it (tenant, vendor context, platform mark) and the principal's claims:
/// <list type="bullet">
/// <item>staff of the host tenant with a tenant role and no vendor context: <see cref="UsageKind.Staff"/>;</item>
/// <item>a vendor context under the Vendor policy (the host tenant's organization included): <see cref="UsageKind.Vendor"/>;</item>
/// <item>a platform admin with OTP on the platform host: <see cref="UsageKind.Platform"/>;</item>
/// <item>anything else, anonymous, an applicant, a vendor on the join page of a tenant it has not joined: not counted (null).</item>
/// </list>
/// A session W-21 has ended is not counted either; the circuit handler checks that, since the principal does not show it.
/// </summary>
internal static class UsageKinds
{
    public static UsageKind? Of(ClaimsPrincipal? user, ITenantAccessor tenants, IVendorAccessor vendor, IPlatformRequestContext platform)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(platform);
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(user.FindFirst(IdentityClaims.Subject)?.Value))
        {
            return null;
        }

        if (platform.IsPlatform)
        {
            return IdentityModule.IsPlatformAdmin(user) ? UsageKind.Platform : null;
        }

        if (tenants.Current is not { } tenant)
        {
            return null;
        }

        if (vendor.Current is not null)
        {
            return IdentityModule.IsTenantVendor(user, tenant) ? UsageKind.Vendor : null;
        }

        return IdentityModule.IsTenantStaff(user, tenant) ? UsageKind.Staff : null;
    }

    /// <summary>The value of <see cref="TelemetryNames.Tags.UserKind"/> for a kind.</summary>
    public static string TagValue(UsageKind kind) => kind switch
    {
        UsageKind.Staff => TelemetryNames.UserKinds.Staff,
        UsageKind.Vendor => TelemetryNames.UserKinds.Vendor,
        UsageKind.Platform => TelemetryNames.UserKinds.Platform,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
