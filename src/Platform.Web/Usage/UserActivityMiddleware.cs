using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Web.Usage;

/// <summary>
/// Active users (spec 6.4): after <c>VendorContextMiddleware</c>, an authorized request of a staff or vendor session
/// (<see cref="UsageKinds"/>) records its user as active through <see cref="IUserActivityRecorder"/>, at most once an hour
/// per user, tenant and kind. Nothing is recorded for anonymous or uncounted sessions, for platform admins, or for the
/// requests of <see cref="UsageRequests.IsRecorded"/>: health and liveness, static files, and the Blazor hub (a circuit's
/// activity is recorded by <see cref="UsageCircuitHandler"/>). The recorder never throws, so the request always goes on.
/// </summary>
internal sealed class UserActivityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, IUserActivityRecorder activity, ITenantAccessor tenants, IVendorAccessor vendor, IPlatformRequestContext platform)
    {
        if (UsageRequests.IsRecorded(context.Request.Path)
            && UsageKinds.Of(context.User, tenants, vendor, platform) is (UsageKind.Staff or UsageKind.Vendor) and var kind)
        {
            await activity.RecordAsync(kind == UsageKind.Vendor ? ActivityKind.Vendor : ActivityKind.Staff, context.RequestAborted);
        }

        await next(context);
    }
}

/// <summary>Which request paths count as a sign of use (spec 6.4).</summary>
internal static class UsageRequests
{
    // Exact paths and prefixes that are never use: health and liveness (W-10 task 4 adds /alive), the framework's and the
    // UI library's static files, the public branding logo, favicons, and the Blazor hub.
    private static readonly PathString[] Exact = ["/health", "/alive", "/favicon.ico", "/favicon.png"];

    private static readonly PathString[] Prefixes = ["/_framework", "/_content", "/_blazor", "/css", "/fonts", "/js", "/branding/logo"];

    public static bool IsRecorded(PathString path) =>
        !Exact.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase))
        && !Prefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))
        && !Path.HasExtension(path.Value);
}
