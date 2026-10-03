using System.Diagnostics;
using OpenTelemetry;

namespace Platform.Shared.Telemetry;

/// <summary>
/// The last processor of both hosts' tracer pipelines, before the exporters (W-10, plan task 3; spec O-10, O-11, section
/// 7.1). When a span ends: <c>url.query</c>, <c>user_agent.original</c> (a request header value, O-11), every
/// <c>http.request.header.*</c> and <c>http.response.header.*</c> tag and every tag under a secret key name
/// (<see cref="TelemetryRedactor.IsSecretKey"/>, such as Npgsql's <c>db.npgsql.data_source</c>, a connection string when the
/// data source has no name) are removed; <c>url.full</c> loses its query and fragment; every other string tag (and string array tag), the display name and
/// the status description pass through <see cref="TelemetryRedactor"/>.
/// </summary>
/// <remarks>
/// Span events never leave (W-10 follow-up, fix round 1, 2026-10-02): <see cref="TelemetryModule"/> sets the OTLP exporter's
/// span event limit to 0 (<see cref="TelemetryModule.SpanEventCountLimit"/>), so the exporter writes no event, only their
/// number. An event cannot be masked (since .NET 10 an <see cref="ActivityEvent"/> keeps its tags in an internal read-only
/// list) nor removed, and in the otel mapping each one would be a <c>logs-*</c> document of its own (Npgsql records
/// <c>received-first-response</c> on every command). So this processor no longer looks at what an event holds; it only
/// copies the type of an <c>exception</c> event onto the span as <c>exception.type</c> when the span has none, so a failed
/// database command or a .NET 10 HttpClient failure still names its exception, as <see cref="SpanExceptions"/> does for
/// requests. The masked message and stack are on a log record of the same trace only when something logs the exception (the
/// exception handler for a request, the job filter for a job); a caller that catches it without logging leaves only the
/// type and the Error status.
/// </remarks>
internal sealed class RedactingSpanProcessor : BaseProcessor<Activity>
{
    private const string UrlQuery = "url.query";
    private const string UserAgent = "user_agent.original";
    private const string UrlFull = "url.full";
    private const string RequestHeaderPrefix = "http.request.header.";
    private const string ResponseHeaderPrefix = "http.response.header.";

    private const string ExceptionEvent = "exception";

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        KeepExceptionType(data);

        var changed = false;
        foreach (ref readonly var tag in data.EnumerateTagObjects())
        {
            if (Replacement(tag.Key, tag.Value).Changed)
            {
                changed = true;
                break;
            }
        }

        if (changed)
        {
            // SetTag changes only the first tag with a key, and AddTag allows the same key twice: every tag is taken off and the
            // masked list put back in its order, so no second occurrence keeps its value.
            var tags = new List<KeyValuePair<string, object?>>();
            foreach (ref readonly var tag in data.EnumerateTagObjects())
            {
                tags.Add(tag);
            }

            foreach (var (key, _) in tags)
            {
                data.SetTag(key, null);
            }

            foreach (var (key, value) in tags)
            {
                if (Replacement(key, value) is var (_, masked) && masked is not null)
                {
                    data.AddTag(key, masked);
                }
            }
        }

        if (data.StatusDescription is { } description && TelemetryRedactor.Redact(description) is var status && !ReferenceEquals(status, description))
        {
            data.SetStatus(data.Status, status);
        }

        if (TelemetryRedactor.Redact(data.DisplayName) is var name && !ReferenceEquals(name, data.DisplayName))
        {
            data.DisplayName = name;
        }
    }

    /// <summary>The type of the span's first <c>exception</c> event as <c>exception.type</c>, unless the span has one.</summary>
    private static void KeepExceptionType(Activity data)
    {
        if (data.GetTagItem(TelemetryNames.Attributes.ExceptionType) is not null)
        {
            return;
        }

        foreach (ref readonly var activityEvent in data.EnumerateEvents())
        {
            if (activityEvent.Name != ExceptionEvent)
            {
                continue;
            }

            foreach (ref readonly var tag in activityEvent.EnumerateTagObjects())
            {
                if (tag.Key == TelemetryNames.Attributes.ExceptionType && tag.Value is string type)
                {
                    data.SetTag(TelemetryNames.Attributes.ExceptionType, type);
                    return;
                }
            }
        }
    }

    private static (bool Changed, object? Value) Replacement(string key, object? value)
    {
        if (key is UrlQuery or UserAgent || key.StartsWith(RequestHeaderPrefix, StringComparison.Ordinal)
            || key.StartsWith(ResponseHeaderPrefix, StringComparison.Ordinal) || TelemetryRedactor.IsSecretKey(key))
        {
            return (true, null);
        }

        if (key == UrlFull && value is string url)
        {
            var end = url.AsSpan().IndexOfAny('?', '#');
            var withoutQuery = end < 0 ? url : url[..end];
            var masked = TelemetryRedactor.Redact(withoutQuery);
            return (!ReferenceEquals(masked, url), masked);
        }

        return NumericRedaction.IsLongNumber(value) && !NumericRedaction.IsSpanMeasurement(key)
            ? (true, TelemetryRedactor.DigitsMarker)
            : Mask(value);
    }

    private static (bool Changed, object? Value) Mask(object? value)
    {
        switch (value)
        {
            case string text:
                var masked = TelemetryRedactor.Redact(text);
                return (!ReferenceEquals(masked, text), masked);
            case string[] texts:
                string[]? copy = null;
                for (var i = 0; i < texts.Length; i++)
                {
                    var element = texts[i] is null ? null : TelemetryRedactor.Redact(texts[i]);
                    if (copy is null && !ReferenceEquals(element, texts[i]))
                    {
                        copy = [.. texts];
                    }

                    if (copy is not null)
                    {
                        copy[i] = element!;
                    }
                }

                return (copy is not null, copy ?? texts);
            default:
                return (false, value);
        }
    }
}
