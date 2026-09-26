namespace Platform.Modules.Audit.Contracts;

/// <summary>One append-only audit event for the current tenant (F-41 basis).</summary>
public sealed record AuditEntry(
    string? ActorId,
    string Action,
    string SubjectType,
    string? SubjectId,
    IReadOnlyDictionary<string, string?>? Data = null);

public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
