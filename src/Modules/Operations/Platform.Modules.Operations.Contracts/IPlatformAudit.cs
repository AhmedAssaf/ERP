namespace Platform.Modules.Operations.Contracts;

/// <summary>One append-only platform-level audit entry: actions taken by platform staff, never a tenant event (D-8).</summary>
public sealed record PlatformAuditEntry(
    string? ActorId,
    string Action,
    string SubjectType,
    string? SubjectId,
    IReadOnlyDictionary<string, string?>? Data = null);

public interface IPlatformAudit
{
    Task WriteAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default);
}
