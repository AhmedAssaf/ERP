using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Platform.Shared.Telemetry;

/// <summary>
/// Adds <c>waslabid.component</c> to every log record, from the logger category (Serilog's <c>SourceContext</c>) by the fixed
/// table of spec section 5.3, so F-53 can group errors per component (W-10, plan task 3). The PostgreSQL, MinIO, SMTP and Web
/// names are the <c>HealthComponents</c> names (Operations module), so a board tile and its errors share a word; this
/// project cannot reference that module, and a unit test keeps the two equal.
/// </summary>
public sealed class ComponentEnricher(string serviceName) : ILogEventEnricher
{
    private const string ModulePrefix = "Platform.Modules.";

    private readonly ConcurrentDictionary<string, LogEventProperty> _byCategory = new(StringComparer.Ordinal);
    private readonly LogEventProperty _service = Property(serviceName);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var property = logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value) && value is ScalarValue { Value: string category }
            ? _byCategory.GetOrAdd(category, c => Property(ComponentOf(c, serviceName)))
            : _service;
        logEvent.AddPropertyIfAbsent(property);
    }

    /// <summary>
    /// <c>Platform.Modules.&lt;Name&gt;.*</c> gives the module name; <c>Npgsql*</c> and <c>Microsoft.EntityFrameworkCore*</c>
    /// give PostgreSQL, <c>Amazon.*</c> MinIO, <c>MailKit*</c> SMTP, <c>Hangfire*</c> Jobs, <c>Microsoft.AspNetCore.*</c> and
    /// <c>Platform.Web.*</c> Web; anything else, or no category, gives <paramref name="serviceName"/>.
    /// </summary>
    public static string ComponentOf(string? category, string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (string.IsNullOrEmpty(category))
        {
            return serviceName;
        }

        if (category.StartsWith(ModulePrefix, StringComparison.Ordinal))
        {
            var name = category.AsSpan(ModulePrefix.Length);
            var end = name.IndexOf('.');
            name = end < 0 ? name : name[..end];
            return name.IsEmpty ? serviceName : name.ToString();
        }

        return category switch
        {
            _ when category.StartsWith("Npgsql", StringComparison.Ordinal) => "PostgreSQL",
            _ when category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) => "PostgreSQL",
            _ when category.StartsWith("Amazon.", StringComparison.Ordinal) => "MinIO",
            _ when category.StartsWith("MailKit", StringComparison.Ordinal) => "SMTP",
            _ when category.StartsWith("Hangfire", StringComparison.Ordinal) => "Jobs",
            _ when IsUnder(category, "Microsoft.AspNetCore") || IsUnder(category, "Platform.Web") => "Web",
            _ => serviceName,
        };
    }

    private static bool IsUnder(string category, string prefix) =>
        category.StartsWith(prefix, StringComparison.Ordinal) && (category.Length == prefix.Length || category[prefix.Length] == '.');

    private static LogEventProperty Property(string component) => new(TelemetryNames.Attributes.Component, new ScalarValue(component));
}
