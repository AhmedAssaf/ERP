using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations;

internal sealed class PlatformAuditWriter(IDbContextFactory<OperationsDbContext> contexts, TimeProvider clock) : IPlatformAudit
{
    private static readonly Dictionary<string, string?> NoData = [];

    public async Task WriteAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.PlatformAudit.Add(new PlatformAuditRow
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = clock.GetUtcNow(),
            ActorId = entry.ActorId,
            Action = entry.Action,
            SubjectType = entry.SubjectType,
            SubjectId = entry.SubjectId,
            Data = JsonSerializer.Serialize(entry.Data ?? NoData),
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
