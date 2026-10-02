using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Platform.Shared.Telemetry;

namespace Platform.Web.Telemetry;

/// <summary>
/// Gives the server span of a request whose exception the exception handler handles (outside Development) its
/// <c>exception.type</c> and status Error (W-10, plan task 3; spec O-10). The ASP.NET Core instrumentation's
/// <c>EnrichWithException</c> runs only for an exception that leaves the pipeline or reaches the developer exception page,
/// not for one the exception handler middleware handles; and no exception event is recorded in either case, since an event
/// cannot be masked. Never handles the exception itself: the default problem details response follows, and the middleware
/// writes the Error log record whose masked message shares the span's trace id.
/// </summary>
internal sealed class ExceptionSpanHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (httpContext.Features.Get<IHttpActivityFeature>()?.Activity is { } activity)
        {
            SpanExceptions.Record(activity, exception);
        }

        return ValueTask.FromResult(false);
    }
}
