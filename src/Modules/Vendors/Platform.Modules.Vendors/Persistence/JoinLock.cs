using Microsoft.EntityFrameworkCore;

namespace Platform.Modules.Vendors.Persistence;

/// <summary>
/// W-40: a company's joins of one tenant and the undo of a failed one, kept apart by a transaction-scoped advisory lock
/// (released at commit or rollback, so it needs an open transaction). A join holds it shared from before its Keycloak add
/// until its commit, so parallel joins still run side by side (one relationship, one <c>vendor.joined</c>, as before); the
/// undo holds it exclusive across its relationship re-check and its revoke, so it waits for every join that may have seen
/// the membership, and a join that starts meanwhile waits for the revoke and then adds the membership itself.
/// Key: <c>hashtextextended('vendor.join:' || tenant id || ':' || company id, 0)</c>, the 64-bit form used by
/// <see cref="CompanyLock"/> (<c>vendor.companies:</c>) and <c>vendor.raise_cr_dispute</c> (<c>vendor.cr_disputes:</c>); the
/// prefix keeps the text apart from theirs, and the migrator's and key ring's locks use fixed constants. A hash collision
/// with any of them could only make one wait for the other, never skip a wait. An advisory lock needs no table privilege.
/// </summary>
internal static class JoinLock
{
    /// <summary>
    /// How long the undo waits for the joins holding the lock: past it the undo gives up and leaves the membership in place
    /// (the logged, safe side). Below Npgsql's default command timeout (30 seconds), so PostgreSQL answers first.
    /// </summary>
    public const string UndoWait = "20s";

    /// <summary>The text hashed into the lock key: one key per tenant and company, prefixed apart from every other lock.</summary>
    public static string Key(Guid tenantId, Guid companyId) => $"vendor.join:{tenantId:D}:{companyId:D}";

    /// <summary>A join's hold, shared with the other joins of the company to the tenant.</summary>
    public static Task<int> ShareAsync(VendorsDbContext db, Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"select pg_advisory_xact_lock_shared(hashtextextended({Key(tenantId, companyId)}, 0))", cancellationToken);

    /// <summary>The undo's hold, alone, waiting at most <see cref="UndoWait"/> (a lock timeout fails the statement).</summary>
    public static async Task ExclusiveAsync(VendorsDbContext db, Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"select set_config('lock_timeout', {UndoWait}, true)", cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"select pg_advisory_xact_lock(hashtextextended({Key(tenantId, companyId)}, 0))", cancellationToken);
    }
}
