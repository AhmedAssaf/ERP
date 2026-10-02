using System.Diagnostics;
using System.Reflection;
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
/// An event cannot be changed: since .NET 10 an <see cref="ActivityEvent"/> keeps its tags in an internal read-only list,
/// whatever collection it was created with, and a span's events cannot be removed. So a span with an event whose string tag
/// would be masked is no longer recorded, and no exporter sends it: nothing unmasked leaves. This is the backstop: the
/// ASP.NET Core and HttpClient instrumentations record no exception event at all (<see cref="SpanExceptions"/>), so it acts
/// only on another source's events. A span whose events hold nothing to mask keeps them as they are.
/// <para>
/// A database span (source <c>Npgsql</c>) leaves without any event (W-10 follow-up, 2026-10-02): in the otel mapping each
/// span event is a <c>logs-*</c> document of its own, and Npgsql records <c>received-first-response</c> on every command.
/// Npgsql's own switch (<c>EnableFirstResponseEvent</c>) reaches only the data sources the hosts build, not the module
/// contexts' pool, which comes from a bare connection string (and must stay shared with plain connections for the
/// row-level security pool tests); so the events are taken off here. An exception event leaves its type on the span as
/// <c>exception.type</c> (as <see cref="SpanExceptions"/> does for requests); Npgsql sets the Error status and
/// <c>error.type</c> itself. .NET has no public way to remove an event, so the span's private event list is cleared by
/// reflection; should a runtime rename it, <see cref="CanDropEvents"/> turns false (a unit test fails) and the events
/// stay, still under the masking backstop above.
/// </para>
/// </remarks>
internal sealed class RedactingSpanProcessor : BaseProcessor<Activity>
{
    private const string UrlQuery = "url.query";
    private const string UserAgent = "user_agent.original";
    private const string UrlFull = "url.full";
    private const string RequestHeaderPrefix = "http.request.header.";
    private const string ResponseHeaderPrefix = "http.response.header.";

    private const string ExceptionEvent = "exception";

    /// <summary>The span's private event list (.NET 10: <c>Activity._events</c>); null if a runtime renames it.</summary>
    private static readonly FieldInfo? EventsField = typeof(Activity).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>True when this runtime lets the processor take a database span's events off.</summary>
    internal static bool CanDropEvents => EventsField is not null;

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Source.Name == TelemetryNames.Sources.Npgsql)
        {
            DropEvents(data);
        }

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

    /// <summary>Keeps the type of an exception event as <c>exception.type</c>, then clears the span's events.</summary>
    private static void DropEvents(Activity data)
    {
        string? exceptionType = null;
        var any = false;
        foreach (ref readonly var activityEvent in data.EnumerateEvents())
        {
            any = true;
            if (exceptionType is null && activityEvent.Name == ExceptionEvent)
            {
                foreach (ref readonly var tag in activityEvent.EnumerateTagObjects())
                {
                    if (tag.Key == TelemetryNames.Attributes.ExceptionType && tag.Value is string type)
                    {
                        exceptionType = type;
                        break;
                    }
                }
            }
        }

        if (!any || EventsField is null)
        {
            return;
        }

        if (exceptionType is not null && data.GetTagItem(TelemetryNames.Attributes.ExceptionType) is null)
        {
            data.SetTag(TelemetryNames.Attributes.ExceptionType, exceptionType);
        }

        EventsField.SetValue(data, null);
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

    /// <summary>
    /// True when a tag of the event is under a secret key name, or a string tag holds a value <see cref="TelemetryRedactor"/>
    /// would mask.
    /// </summary>
    private static bool NeedsMasking(in ActivityEvent activityEvent)
    {
        foreach (ref readonly var tag in activityEvent.EnumerateTagObjects())
        {
            if (TelemetryRedactor.IsSecretKey(tag.Key) || Mask(tag.Value).Changed)
            {
                return true;
            }
        }

        return false;
    }
}
