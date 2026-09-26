using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Web.Tenancy;

/// <summary>Resolves the tenant from the host name before authentication; an unknown host is a 404 (spec 2.1).</summary>
internal sealed class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantDirectory directory, TenantAccessor accessor)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        var tenant = await directory.FindByHostAsync(context.Request.Host.Host, context.RequestAborted);
        if (tenant is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        accessor.Set(tenant);
        await next(context);
    }
}
