namespace Platform.Modules.Audit;

internal sealed class AuditEvent
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? ActorId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string? SubjectId { get; set; }

    public string Data { get; set; } = "{}";
}
