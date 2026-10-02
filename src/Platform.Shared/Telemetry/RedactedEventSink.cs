using System.Runtime.CompilerServices;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Platform.Shared.Telemetry;

/// <summary>
/// Wraps each sink of the host's Serilog logger (the OTLP sink, and any other <see cref="ILogEventSink"/> service such as a
/// test's in-memory sink; W-10, plan task 3, spec 5.3). An enricher cannot replace <see cref="LogEvent.Exception"/>, and the
/// OTLP sink would export the raw exception message from it; so a sink here receives a copy of an event that had an
/// exception, with <see cref="LogEvent.Exception"/> null (<see cref="RedactingEnricher"/>'s <c>exception.type</c>,
/// <c>exception.message</c> and <c>exception.stacktrace</c> carry the type, the masked message and the masked stack). The
/// text of the message template is masked as well: a library's literal text (a template with no property tokens, or the
/// literal parts around them) never reaches a sink unmasked. An event with neither is passed on as it is.
/// </summary>
internal sealed class RedactedEventSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    /// <summary>Masked templates by template; Serilog caches parsed templates, so most events find theirs here.</summary>
    private static readonly ConditionalWeakTable<MessageTemplate, MessageTemplate> Templates = [];

    private int _disposed;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var template = Templates.GetValue(logEvent.MessageTemplate, Redact);
        if (logEvent.Exception is null && ReferenceEquals(template, logEvent.MessageTemplate))
        {
            inner.Emit(logEvent);
            return;
        }

        inner.Emit(new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            exception: null,
            template,
            logEvent.Properties.Select(p => new LogEventProperty(p.Key, p.Value)),
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default));
    }

    /// <summary>Disposes the wrapped sink (the OTLP sink flushes its last batch); a second call does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            (inner as IDisposable)?.Dispose();
        }
    }

    /// <summary>The template with its text and its literal tokens masked; the same instance when nothing changed.</summary>
    private static MessageTemplate Redact(MessageTemplate template)
    {
        var changed = false;
        var tokens = new List<MessageTemplateToken>();
        foreach (var token in template.Tokens)
        {
            if (token is TextToken text && TelemetryRedactor.Redact(text.Text) is var masked && !ReferenceEquals(masked, text.Text))
            {
                tokens.Add(new TextToken(masked));
                changed = true;
            }
            else
            {
                tokens.Add(token);
            }
        }

        var maskedText = TelemetryRedactor.Redact(template.Text);
        return changed || !ReferenceEquals(maskedText, template.Text) ? new MessageTemplate(maskedText, tokens) : template;
    }
}
