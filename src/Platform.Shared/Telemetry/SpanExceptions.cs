using System.Diagnostics;

namespace Platform.Shared.Telemetry;

/// <summary>
/// What a span keeps of an exception (W-10, plan task 3; spec O-10, section 7.1): status Error and <c>exception.type</c>, the
/// full type name, never an exception event. An event cannot be masked once recorded (since .NET 10 its tags are read-only),
/// so the ASP.NET Core and HttpClient instrumentations record none (<c>RecordException</c> off) and call this from their
/// <c>EnrichWithException</c> slot instead. The masked message and stack are on the log record of the same trace
/// (<see cref="RedactingEnricher"/>). Setting the status without a description also replaces any description an
/// instrumentation took from the exception's message.
/// </summary>
public static class SpanExceptions
{
    public static void Record(Activity activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(exception);
        activity.SetTag(TelemetryNames.Attributes.ExceptionType, exception.GetType().FullName);
        activity.SetStatus(ActivityStatusCode.Error);
    }
}
