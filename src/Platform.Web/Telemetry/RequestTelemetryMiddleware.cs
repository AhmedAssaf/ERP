using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Platform.Shared.Tenancy;

namespace Platform.Web.Telemetry;

/// <summary>
/// O-7: every response names its W3C trace id in <see cref="Header"/>, the id to paste into Kibana (F-53); no second id is
/// invented. The first middleware of the pipeline, so the platform host's and the tenant resolution's own 404s, the
/// exception handler's 500 and <c>/health</c> carry it too: the header is added when the response starts, whoever writes it.
/// An untraced request (<c>/health</c>) still has its activity, and so its trace id, only no exported span.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string Header = "X-Correlation-Id";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (RequestSpan.Of(context) is { IdFormat: ActivityIdFormat.W3C } activity)
        {
            context.Response.OnStarting(
                static state =>
                {
                    var (response, traceId) = ((HttpResponse, string))state;
                    response.Headers[Header] = traceId;
                    return Task.CompletedTask;
                },
                (context.Response, activity.TraceId.ToHexString()));
        }

        return next(context);
    }
}

/// <summary>
/// O-9 on tenant hosts: directly after <c>TenantMiddleware</c>, a request with a tenant tags its server span with the tenant's
/// id and slug and opens a log scope with the same pair for the rest of the request, which the Serilog provider turns into
/// properties of every record written below it. The platform host and <c>/health</c> have no tenant and get neither.
/// </summary>
internal sealed class RequestTelemetryMiddleware(RequestDelegate next, ILogger<RequestTelemetryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ITenantAccessor tenants)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenants);
        if (tenants.Current is not { } tenant)
        {
            await next(context);
            return;
        }

        var tags = TelemetryContext.Tenant(tenant);
        TelemetryContext.Tag(RequestSpan.Of(context), tags);
        using (logger.BeginScope(tags))
        {
            await next(context);
        }
    }
}

/// <summary>The server span of a request: the activity ASP.NET Core hosting started for it.</summary>
internal static class RequestSpan
{
    public static Activity? Of(HttpContext context) => context.Features.Get<IHttpActivityFeature>()?.Activity ?? Activity.Current;
}
