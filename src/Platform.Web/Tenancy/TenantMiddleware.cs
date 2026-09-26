using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Web.Tenancy;

/// <summary>Resolves the tenant from the host name before authentication; an unknown host is a 404 (spec 2.1).</summary>
internal sealed class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantDirectory directory, TenantAccessor accessor)
    {
        // Only the exact health path skips tenant resolution; /health/anything is an ordinary tenant path.
        if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var host = context.Request.Host.Host;
        var tenant = string.IsNullOrWhiteSpace(host) ? null : await directory.FindByHostAsync(host, context.RequestAborted);
        if (tenant is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        accessor.Set(tenant);
        await next(context);
    }
}
