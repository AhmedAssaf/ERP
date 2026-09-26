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
        ComponentDown(incident.Component, incident.OpenedAt, incident.LastMessage, boardUrl);

    public static AlertMessage IncidentClosed(IncidentRow incident, string boardUrl) =>
        ComponentRecovered(incident.Component, incident.ClosedAt ?? incident.OpenedAt, incident.LastMessage, boardUrl);

    /// <summary>
    /// Also sent by <see cref="Health.HealthCheckJob"/> without an incident row when the health store is down; the
    /// message is the check's own description, which never carries a secret (<see cref="Health.HealthCheckMessages"/>).
    /// </summary>
    public static AlertMessage ComponentDown(string component, DateTimeOffset at, string? message, string boardUrl) =>
        new($"[WaslaBid] {component} is down", Body(component, at, message, boardUrl));

    public static AlertMessage ComponentRecovered(string component, DateTimeOffset at, string? message, string boardUrl) =>
        new($"[WaslaBid] {component} has recovered", Body(component, at, message, boardUrl));

    /// <summary>
    /// The health results cannot be written (typically PostgreSQL itself is down). N-10: only the exception's type
    /// name, never its message, which for a connection failure can echo a host, user, or connection string.
    /// </summary>
    public static AlertMessage HealthStoreUnavailable(string errorType, DateTimeOffset at, string boardUrl) =>
        new(
            "[WaslaBid] WaslaBid cannot record health results",
            $"""
             The health-check job could not record its results; alerts are sent from the worker's memory until it can.
             Error type: {errorType}
             {TimeLines(at)}
             Board: {boardUrl}
             """);

    /// <summary>
    /// N-10: <paramref name="jobDisplayName"/> is <c>Type.Method</c> only, never the job's arguments (see
    /// <see cref="JobFailureAlertFilter"/>); <paramref name="recurringJobId"/> is a developer-chosen id, not data.
    /// </summary>
    public static AlertMessage JobFailedThreeTimes(
        string jobId, string jobDisplayName, string? recurringJobId, DateTimeOffset at, string boardUrl) =>
        new(
            $"[WaslaBid] Job {jobDisplayName} failed three times in a row",
            $"""
             Job id: {jobId}
             Job: {jobDisplayName}
             Recurring job: {recurringJobId ?? "(none)"}
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
