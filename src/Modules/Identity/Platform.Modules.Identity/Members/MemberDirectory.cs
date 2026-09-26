using Microsoft.EntityFrameworkCore;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity.Members;

/// <summary>
/// <c>identity.members</c> for the current tenant (F-07, spec D-3 and 4.1). Row-level security scopes every query to the
/// tenant the request or circuit resolved; the query filter mirrors it.
/// </summary>
internal sealed class MemberDirectory(
    IDbContextFactory<MembersDbContext> contexts, ITenantAccessor tenants, IAuditWriter audit, TimeProvider clock) : IMemberDirectory
{
    public async Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var roles = await db.Members.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.Roles).SingleOrDefaultAsync(cancellationToken);
        return roles ?? [];
    }

    public async Task<IReadOnlyList<Member>> ListAsync(CancellationToken cancellationToken = default)
    {
        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Members.AsNoTracking().OrderBy(m => m.DisplayName).ThenBy(m => m.Email).ToListAsync(cancellationToken);
        return [.. rows.Select(ToMember)];
    }

    public async Task<Result<Member>> SetRolesAsync(
        string userId, IReadOnlyCollection<string> roles, string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        RequireTenant();

        var unknown = roles.Where(r => !TenantRoles.All.Contains(r, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            return Result.Failure<Member>(Error.Validation("identity.unknown_role", $"These are not tenant roles: {string.Join(", ", unknown)}."));
        }

        // Stored in the fixed order of TenantRoles.All, without duplicates.
        var wanted = TenantRoles.All.Where(r => roles.Contains(r, StringComparer.Ordinal)).ToList();

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Two admins removing each other's admin role at once must not both pass the check below: the tenant's admin rows
        // are locked first, so the second change waits and then counts what the first one left.
        await db.Database.ExecuteSqlAsync(
            $"select 1 from identity.members where 'tenant-admin' = any(roles) for update", cancellationToken);

        var member = await db.Members.SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (member is null)
        {
            return Result.Failure<Member>(Error.NotFound("identity.member_not_found", "The user is not a member of this tenant."));
        }

        var dropsAdmin = member.Roles.Contains(TenantRoles.TenantAdmin) && !wanted.Contains(TenantRoles.TenantAdmin);
        if (dropsAdmin && member.Status == MemberStatuses.Active)
        {
            var otherAdmins = await db.Members.CountAsync(
                m => m.Id != member.Id && m.Status == MemberStatuses.Active && m.Roles.Contains(TenantRoles.TenantAdmin), cancellationToken);
            if (otherAdmins == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                await audit.WriteAsync(
                    new AuditEntry(actorId, "identity.last_admin_protected", "member", userId, Data(member.Roles, wanted)), cancellationToken);
                return Result.Failure<Member>(Error.Refused(
                    "identity.last_tenant_admin", "The tenant must keep at least one active tenant admin, so this admin role cannot be removed."));
            }
        }

        var before = member.Roles.ToList();
        member.Roles = wanted;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await audit.WriteAsync(new AuditEntry(actorId, "identity.roles_changed", "member", userId, Data(before, wanted)), cancellationToken);
        return Result.Success(ToMember(member));
    }

    /// <summary>
    /// The member's roles at sign-in (spec 4.1), for the members claims transformation. The row is found by the Keycloak
    /// <c>sub</c>; failing that, a row without a user id is bound to it when <paramref name="verifiedEmail"/> matches (the
    /// development seed). An invited member becomes active here. Returns no roles when there is no row.
    /// </summary>
    internal async Task<IReadOnlyList<string>> SignInAsync(string userId, string? verifiedEmail, CancellationToken cancellationToken)
    {
        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var member = await db.Members.SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (member is null && !string.IsNullOrWhiteSpace(verifiedEmail))
        {
            var email = verifiedEmail.Trim().ToLowerInvariant();
            var now = clock.GetUtcNow();
            // A conditional update, so of two first requests at once only one binds; the other then finds the row by sub.
            var bound = await db.Members
                .Where(m => m.UserId == null && m.Email == email)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(m => m.UserId, userId)
                        .SetProperty(m => m.Status, MemberStatuses.Active)
                        .SetProperty(m => m.ActivatedAt, now),
                    cancellationToken);
            member = await db.Members.SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);
            if (bound == 1 && member is not null)
            {
                await AuditActivatedAsync(userId, "email", cancellationToken);
            }
        }

        if (member is null)
        {
            return [];
        }

        if (member.Status == MemberStatuses.Invited)
        {
            var activated = await db.Members
                .Where(m => m.Id == member.Id && m.Status == MemberStatuses.Invited)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(m => m.Status, MemberStatuses.Active).SetProperty(m => m.ActivatedAt, clock.GetUtcNow()),
                    cancellationToken);
            if (activated == 1)
            {
                await AuditActivatedAsync(userId, "sub", cancellationToken);
            }
        }

        return member.Roles;
    }

    private Task AuditActivatedAsync(string userId, string matchedBy, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            new AuditEntry(userId, "identity.member_activated", "member", userId, new Dictionary<string, string?> { ["matched_by"] = matchedBy }),
            cancellationToken);

    private void RequireTenant()
    {
        if (tenants.Current is null)
        {
            throw new InvalidOperationException("Members belong to a tenant; this request or circuit has none.");
        }
    }

    private static Dictionary<string, string?> Data(IEnumerable<string> before, IEnumerable<string> after) => new()
    {
        ["before"] = string.Join(",", before),
        ["after"] = string.Join(",", after),
    };

    private static Member ToMember(MemberRecord row) => new(
        row.UserId,
        row.Email,
        row.DisplayName,
        row.Roles,
        row.Status == MemberStatuses.Active ? MemberStatus.Active : MemberStatus.Invited,
        row.InvitedAt,
        row.ActivatedAt);
}
