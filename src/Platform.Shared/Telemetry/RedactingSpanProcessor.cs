using System.Diagnostics;
using OpenTelemetry;

namespace Platform.Shared.Telemetry;

/// <summary>
/// The last processor of both hosts' tracer pipelines, before the exporters (W-10, plan task 3; spec O-10, O-11, section
/// 7.1). When a span ends: <c>url.query</c> and every <c>http.request.header.*</c> and <c>http.response.header.*</c> tag are
/// removed; <c>url.full</c> loses its query and fragment; every other string tag (and string array tag), the display name and
/// the status description pass through <see cref="TelemetryRedactor"/>.
/// </summary>
/// <remarks>
/// An event cannot be changed: since .NET 10 an <see cref="ActivityEvent"/> keeps its tags in an internal read-only list,
/// whatever collection it was created with, and a span's events cannot be removed. So a span with an event whose string tag
/// would be masked is no longer recorded, and no exporter sends it: nothing unmasked leaves. This is the backstop: the
/// ASP.NET Core and HttpClient instrumentations record no exception event at all (<see cref="SpanExceptions"/>), so it acts
/// only on another source's events, such as Npgsql's exception event for a failed command. A span whose events hold
/// nothing to mask keeps them as they are.
/// </remarks>
internal sealed class RedactingSpanProcessor : BaseProcessor<Activity>
{
    private const string UrlQuery = "url.query";
    private const string UrlFull = "url.full";
    private const string RequestHeaderPrefix = "http.request.header.";
    private const string ResponseHeaderPrefix = "http.response.header.";

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
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

        foreach (ref readonly var activityEvent in data.EnumerateEvents())
        {
            if (NeedsMasking(activityEvent))
            {
                data.IsAllDataRequested = false;
                data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                return;
            }
        }
    }

    private static (bool Changed, object? Value) Replacement(string key, object? value)
    {
        if (key == UrlQuery || key.StartsWith(RequestHeaderPrefix, StringComparison.Ordinal) || key.StartsWith(ResponseHeaderPrefix, StringComparison.Ordinal))
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

        return Mask(value);
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

    /// <summary>True when a string tag of the event holds a value <see cref="TelemetryRedactor"/> would mask.</summary>
    private static bool NeedsMasking(in ActivityEvent activityEvent)
    {
        foreach (ref readonly var tag in activityEvent.EnumerateTagObjects())
        {
            if (Mask(tag.Value).Changed)
            {
                return true;
            }
        }

        return false;
    }
}
