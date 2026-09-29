using Microsoft.EntityFrameworkCore;

namespace Platform.Modules.Vendors.Persistence;

/// <summary>
/// One company's uploads and documents changed one at a time: a transaction-scoped advisory lock keyed on the company
/// (released at commit or rollback). It replaced <c>SELECT ... FOR UPDATE</c> on <c>vendor.companies</c> when the
/// application role lost UPDATE on that table (vendors migration 0019, pentest L-5): a row lock needs UPDATE privilege, an
/// advisory lock needs none. Every caller uses this same key, so they exclude each other as the row lock did.
/// </summary>
internal static class CompanyLock
{
    public static Task<int> AcquireAsync(VendorsDbContext db, Guid companyId, CancellationToken cancellationToken)
    {
        var key = $"vendor.companies:{companyId:D}";
        return db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }
}
