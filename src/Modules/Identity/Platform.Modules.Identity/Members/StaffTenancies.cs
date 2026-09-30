using Microsoft.EntityFrameworkCore;
using Platform.Modules.Identity.Contracts;

namespace Platform.Modules.Identity.Members;

/// <summary>
/// <see cref="IStaffTenancies"/> through <c>identity.staff_tenants_of</c> (migration 0002), which reads
/// <c>identity.members</c> across tenants as its owner and answers a platform console session only.
/// </summary>
internal sealed class StaffTenancies(IDbContextFactory<MembersDbContext> contexts) : IStaffTenancies
{
    public async Task<IReadOnlySet<Guid>> TenantsOfAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var tenants = await db.Database.SqlQuery<Guid>($"select tenant_id as \"Value\" from identity.staff_tenants_of({userId})")
            .ToListAsync(cancellationToken);
        return tenants.ToHashSet();
    }
}
