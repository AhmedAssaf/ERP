namespace Platform.Modules.Operations.Contracts;

/// <summary>F-51 tile status. Degraded is shown but never opens or closes an incident.</summary>
public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy,
}

/// <summary>One health check result for one component (spec section 3.2).</summary>
public sealed record HealthResult(
    string Component,
    HealthStatus Status,
    int LatencyMs,
    DateTimeOffset CheckedAt,
    string? Message = null);

/// <summary>An incident row (F-60), open while ClosedAt is null.</summary>
public sealed record Incident(
    Guid Id,
    string Component,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    string? LastMessage);

public enum IncidentTransitionKind
{
    Opened,
    Closed,
}

/// <summary>An incident state change caused by a single <see cref="IHealthLog.RecordAsync"/> call, for the alert job to act on.</summary>
public sealed record IncidentTransition(IncidentTransitionKind Kind, string Component, Guid IncidentId);
