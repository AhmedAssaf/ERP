using Microsoft.AspNetCore.Http;

namespace Platform.Modules.Identity.Members;

/// <summary>
/// Requests that never need the user's tenant roles: static assets, the health and liveness probes, and the public branding logo. The
/// members claims transformation skips them, so a page load's dozen asset requests do not each read the member table.
/// The Blazor hub (<c>/_blazor</c>) is not one of them: the circuit takes its user, roles included, from that request.
/// </summary>
internal static class StaticRequests
{
    private static readonly PathString[] Prefixes = ["/_framework", "/_content", "/css", "/fonts", "/js", "/branding/logo"];

    private static readonly PathString[] Exact = ["/health", "/alive", "/favicon.ico", "/favicon.png"];

    public static bool IsStatic(PathString path) =>
        Exact.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase))
        || Prefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
}
