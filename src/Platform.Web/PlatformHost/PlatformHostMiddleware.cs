using Microsoft.Extensions.Options;

namespace Platform.Web.PlatformHost;

/// <summary>
/// Separates the platform console's host from tenant hosts before tenant resolution and authentication (spec 3.1, D-1).
/// On the platform host only platform paths and the shared framework paths answer, and the request is marked so
/// <c>TenantMiddleware</c> skips it; any other path is a 404. On every other host a platform path is a 404, so a tenant
/// admin requesting the console URL learns nothing (docs/09 F-51).
/// </summary>
internal sealed class PlatformHostMiddleware(RequestDelegate next, IOptions<PlatformHostOptions> options)
{
    private readonly string? _platformHost = string.IsNullOrWhiteSpace(options.Value.Host) ? null : options.Value.Host.Trim();

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var onPlatformHost = _platformHost is not null
            && string.Equals(context.Request.Host.Host, _platformHost, StringComparison.OrdinalIgnoreCase);

        if (onPlatformHost)
        {
            if (!PlatformRequest.IsPlatformPath(path) && !PlatformRequest.IsSharedPath(path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            PlatformRequest.Mark(context);
            return next(context);
        }

        if (PlatformRequest.IsPlatformPath(path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
