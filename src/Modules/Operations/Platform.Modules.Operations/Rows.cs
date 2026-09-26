using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations;

internal sealed class HealthResultRow
{
    public Guid Id { get; set; }

    public string Component { get; set; } = string.Empty;

    public HealthStatus Status { get; set; }

    public int LatencyMs { get; set; }

    public DateTimeOffset CheckedAt { get; set; }

    public string? Message { get; set; }
}

internal sealed class IncidentRow
{
    public Guid Id { get; set; }

    public string Component { get; set; } = string.Empty;

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public string? LastMessage { get; set; }

    public bool NotifiedOpen { get; set; }

    public bool NotifiedClose { get; set; }
}

internal sealed class PlatformAuditRow
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? ActorId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string? SubjectId { get; set; }

    public string Data { get; set; } = "{}";
}
