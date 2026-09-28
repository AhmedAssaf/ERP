using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Modules.Audit.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Audit;

/// <summary>
/// Writes <c>audit.events</c> for the current tenant. The time is the database's: the column's default and a trigger set
/// <c>occurred_at = now()</c> (audit migration 0003), so the writer names no time, and a vendor session could not name one.
/// A plain insert without RETURNING, since a vendor session may insert into the tenant's log but not read it.
/// </summary>
internal sealed class AuditWriter(IDbContextFactory<AuditDbContext> contexts, ITenantAccessor tenants) : IAuditWriter
{
    private static readonly Dictionary<string, string?> NoData = [];

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var tenant = tenants.Current ?? throw new InvalidOperationException("An audit event needs a current tenant.");

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var id = Guid.CreateVersion7();
        var data = JsonSerializer.Serialize(entry.Data ?? NoData);
        await db.Database.ExecuteSqlAsync($"""
            insert into audit.events (id, tenant_id, actor_id, action, subject_type, subject_id, data)
            values ({id}, {tenant.TenantId}, {entry.ActorId}, {entry.Action}, {entry.SubjectType}, {entry.SubjectId}, {data}::jsonb)
            """, cancellationToken);
    }
}
