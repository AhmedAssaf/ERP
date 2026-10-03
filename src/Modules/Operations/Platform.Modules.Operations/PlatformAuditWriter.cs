using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations;

/// <summary>
/// Writes <c>ops.platform_audit</c> through <c>ops.write_platform_audit</c> (operations migration 0004): the application
/// role holds no INSERT on the table, the function sets the time, and an application-role session (W-41, migration 0008) may write
/// only as its own acting user; a free actor is the worker's.
/// </summary>
internal sealed class PlatformAuditWriter(IDbContextFactory<OperationsDbContext> contexts) : IPlatformAudit
{
    private static readonly Dictionary<string, string?> NoData = [];

    public async Task WriteAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var id = Guid.CreateVersion7();
        var data = JsonSerializer.Serialize(entry.Data ?? NoData);
        await db.Database.ExecuteSqlAsync(
            $"select ops.write_platform_audit({id}, {entry.ActorId}, {entry.Action}, {entry.SubjectType}, {entry.SubjectId}, {data}::jsonb)",
            cancellationToken);
    }
}
