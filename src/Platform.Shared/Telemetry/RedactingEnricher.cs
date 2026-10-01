using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace Platform.Shared.Telemetry;

/// <summary>
/// The last enricher of the host's Serilog logger (W-10, plan task 3; spec O-10, section 5.3): every string property value,
/// also inside structure, sequence and dictionary values (dictionary keys included), passes through
/// <see cref="TelemetryRedactor"/> and is then cut to <see cref="MaximumStringLength"/> (the logger has no string cap of its
/// own, which would cut before masking); a <see cref="QueryString"/> property is blanked. This also covers objects logged with <c>{@...}</c>, whose members
/// <see cref="RedactingDestructuringPolicy"/> leaves to this step. For an event with an exception it adds
/// <c>exception.type</c> (the full type name), <c>exception.message</c> and <c>exception.stacktrace</c> (the text of
/// <see cref="Exception.ToString"/>), both masked; the raw exception itself is taken off the event by
/// <see cref="RedactedEventSink"/>, since an enricher cannot replace <see cref="LogEvent.Exception"/>.
/// </summary>
public sealed class RedactingEnricher : ILogEventEnricher
{
    public const string ExceptionType = TelemetryNames.Attributes.ExceptionType;
    public const string ExceptionMessage = "exception.message";
    public const string ExceptionStackTrace = "exception.stacktrace";

    /// <summary>
    /// The property ASP.NET Core's Hosting request records (<c>Request starting ... {Path}{QueryString}</c>, <c>Request
    /// finished ...</c>) and other framework records name the query string with; its value is always blanked (O-11: no query
    /// string is ever captured), whatever the category. Sinks render the message from the properties, so the rendered
    /// message carries no query string either.
    /// </summary>
    public const string QueryString = "QueryString";

    private static readonly LogEventProperty BlankQueryString = new(QueryString, new ScalarValue(string.Empty));

    /// <summary>The longest string value kept; longer ones are cut after masking, ending in an ellipsis.</summary>
    public const int MaximumStringLength = 4096;

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        List<LogEventProperty>? changed = null;
        foreach (var (name, value) in logEvent.Properties)
        {
            if (name == QueryString)
            {
                if (value is not ScalarValue { Value: null or "" })
                {
                    (changed ??= []).Add(BlankQueryString);
                }

                continue;
            }

            var redacted = Redact(value);
            if (!ReferenceEquals(redacted, value))
            {
                (changed ??= []).Add(new LogEventProperty(name, redacted));
            }
        }

        if (changed is not null)
        {
            foreach (var property in changed)
            {
                logEvent.AddOrUpdateProperty(property);
            }
        }

        if (logEvent.Exception is { } exception)
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(ExceptionType, new ScalarValue(exception.GetType().FullName)));
            logEvent.AddOrUpdateProperty(new LogEventProperty(ExceptionMessage, new ScalarValue(TelemetryRedactor.Redact(exception.Message))));
            logEvent.AddOrUpdateProperty(new LogEventProperty(ExceptionStackTrace, new ScalarValue(TelemetryRedactor.Redact(exception.ToString()))));
        }
    }

    /// <summary>The value with every string in it masked; the same instance when nothing changed.</summary>
    internal static LogEventPropertyValue Redact(LogEventPropertyValue value) => value switch
    {
        ScalarValue scalar => Redact(scalar),
        SequenceValue sequence => Redact(sequence),
        StructureValue structure => Redact(structure),
        DictionaryValue dictionary => Redact(dictionary),
        _ => value,
    };

    private static ScalarValue Redact(ScalarValue scalar)
    {
        var text = scalar.Value switch
        {
            null => null,
            string s => s,
            // Numbers, dates, ids and flags carry no free text; anything else (a Uri, a stringified object) is rendered as text.
            bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly or Guid or Enum => null,
            var other => Convert.ToString(other, CultureInfo.InvariantCulture),
        };
        if (text is null)
        {
            return scalar;
        }

        // Masked first, then cut: a cut first could leave half an address that no longer matches a pattern.
        var redacted = TelemetryRedactor.Redact(text);
        if (redacted.Length > MaximumStringLength)
        {
            redacted = string.Concat(redacted.AsSpan(0, MaximumStringLength - 1), "…");
        }

        return ReferenceEquals(redacted, text) ? scalar : new ScalarValue(redacted);
    }

    private static SequenceValue Redact(SequenceValue sequence)
    {
        LogEventPropertyValue[]? elements = null;
        for (var i = 0; i < sequence.Elements.Count; i++)
        {
            var redacted = Redact(sequence.Elements[i]);
            if (elements is null && !ReferenceEquals(redacted, sequence.Elements[i]))
            {
                elements = [.. sequence.Elements];
            }

            if (elements is not null)
            {
                elements[i] = redacted;
            }
        }

        return elements is null ? sequence : new SequenceValue(elements);
    }

    private static StructureValue Redact(StructureValue structure)
    {
        LogEventProperty[]? properties = null;
        for (var i = 0; i < structure.Properties.Count; i++)
        {
            var property = structure.Properties[i];
            var redacted = Redact(property.Value);
            if (properties is null && !ReferenceEquals(redacted, property.Value))
            {
                properties = [.. structure.Properties];
            }

            if (properties is not null)
            {
                properties[i] = ReferenceEquals(redacted, property.Value) ? property : new LogEventProperty(property.Name, redacted);
            }
        }

        return properties is null ? structure : new StructureValue(properties, structure.TypeTag);
    }

    private static DictionaryValue Redact(DictionaryValue dictionary)
    {
        List<KeyValuePair<ScalarValue, LogEventPropertyValue>>? elements = null;
        var index = 0;
        foreach (var (key, value) in dictionary.Elements)
        {
            var redactedKey = Redact(key);
            var redactedValue = Redact(value);
            if (elements is null && (!ReferenceEquals(redactedKey, key) || !ReferenceEquals(redactedValue, value)))
            {
                elements = [.. dictionary.Elements.Take(index)];
            }

            elements?.Add(new(redactedKey, redactedValue));
            index++;
        }

        // Two keys can mask to the same marker; the first one is kept.
        return elements is null ? dictionary : new DictionaryValue(elements.DistinctBy(e => e.Key));
    }
}
