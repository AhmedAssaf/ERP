using Platform.Shared.Tenancy;

namespace Platform.Web.PlatformHost;

/// <summary>
/// Whether a request belongs to the platform console (spec 3.1). <see cref="PlatformHostMiddleware"/> marks it; tenant
/// resolution, the circuit's tenant, and the choice of cookie and policy read the mark.
/// </summary>
internal static class PlatformRequest
{
    private static readonly object Key = new();

    /// <summary>Console pages and the platform scheme's sign-in and sign-out callbacks.</summary>
    private static readonly PathString[] PlatformPaths =
    [
        "/platform",
        PlatformAuthentication.CallbackPath,
        PlatformAuthentication.SignedOutCallbackPath,
        PlatformAuthentication.RemoteSignOutPath,
    ];

    /// <summary>Framework paths that both kinds of host serve: static assets, the Blazor circuit, the culture switch.</summary>
    private static readonly PathString[] SharedPaths = ["/_framework", "/_content", "/_blazor", "/culture"];

    /// <summary>
    /// Marks the request, and the request's scoped <see cref="PlatformRequestContext"/> that module services such as
    /// <c>ITenantCatalog</c> read, since they have no <see cref="HttpContext"/>.
    /// </summary>
    public static void Mark(HttpContext context)
    {
        context.Items[Key] = true;
        context.RequestServices?.GetService<PlatformRequestContext>()?.MarkPlatform();
    }

    public static bool IsPlatform(HttpContext context) => context.Items.ContainsKey(Key);

    public static bool IsPlatformPath(PathString path) =>
        PlatformPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    public static bool IsSharedPath(PathString path) =>
        IsProbePath(path) || SharedPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Readiness (<c>/health</c>) and liveness (<c>/alive</c>, W-10 O-15): answered on every host without a tenant. Only the
    /// exact paths; a path below them is an ordinary path.
    /// </summary>
    public static bool IsProbePath(PathString path) =>
        path.Equals("/health", StringComparison.OrdinalIgnoreCase) || path.Equals("/alive", StringComparison.OrdinalIgnoreCase);
}
