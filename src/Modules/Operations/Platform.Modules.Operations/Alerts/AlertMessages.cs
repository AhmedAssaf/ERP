using System.Globalization;

namespace Platform.Modules.Operations.Alerts;

/// <summary>
/// Spec 3.3 "Email content": subject <c>[WaslaBid] &lt;component&gt; is down</c> / <c>... has recovered</c>; body
/// with component, time (Riyadh time and UTC), message, and a link to the board; never a secret value.
/// </summary>
internal static class AlertMessages
{
    // Riyadh (AST) is UTC+3 year-round, no daylight saving; a fixed offset avoids depending on the host having the
    // "Asia/Riyadh" / "Arab Standard Time" zone installed (Windows and Linux name it differently).
    private static readonly TimeSpan RiyadhOffset = TimeSpan.FromHours(3);

    public static AlertMessage IncidentOpened(IncidentRow incident, string boardUrl) =>
        new(
            $"[WaslaBid] {incident.Component} is down",
            Body(incident.Component, incident.OpenedAt, incident.LastMessage, boardUrl));

    public static AlertMessage IncidentClosed(IncidentRow incident, string boardUrl) =>
        new(
            $"[WaslaBid] {incident.Component} has recovered",
            Body(incident.Component, incident.ClosedAt ?? incident.OpenedAt, incident.LastMessage, boardUrl));

    public static AlertMessage JobFailedThreeTimes(string jobId, string jobDisplayName, DateTimeOffset at, string boardUrl) =>
        new(
            $"[WaslaBid] Job {jobDisplayName} failed three times in a row",
            $"""
             Job id: {jobId}
             Job: {jobDisplayName}
             {TimeLines(at)}
             Board: {boardUrl}
             """);

    private static string Body(string component, DateTimeOffset at, string? message, string boardUrl) =>
        $"""
         Component: {component}
         {TimeLines(at)}
         Message: {message ?? "(none)"}
         Board: {boardUrl}
         """;

    private static string TimeLines(DateTimeOffset at) =>
        $"""
         Time (UTC): {at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}
         Time (Riyadh): {at.ToOffset(RiyadhOffset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}
         """;
}
