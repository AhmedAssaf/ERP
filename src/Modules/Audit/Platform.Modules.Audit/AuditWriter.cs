using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Modules.Audit.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Audit;

internal sealed class AuditWriter(IDbContextFactory<AuditDbContext> contexts, ITenantAccessor tenants, TimeProvider clock) : IAuditWriter
{
    private static readonly Dictionary<string, string?> NoData = [];

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var tenant = tenants.Current ?? throw new InvalidOperationException("An audit event needs a current tenant.");

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.Events.Add(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.TenantId,
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
