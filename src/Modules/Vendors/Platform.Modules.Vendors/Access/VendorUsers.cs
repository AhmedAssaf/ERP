using Microsoft.EntityFrameworkCore;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;

namespace Platform.Modules.Vendors.Access;

/// <summary>
/// The company of a user through <c>vendor.company_of_user</c> (migration 0002), the one read of
/// <c>vendor.vendor_users</c> that works before a vendor context exists. Scoped: the Vendor policy and the host that sets
/// the vendor context after it ask about the same user in one request or circuit, so the answer is kept for the scope.
/// </summary>
internal sealed class VendorUsers(IDbContextFactory<VendorsDbContext> contexts) : IVendorUsers
{
    private readonly Dictionary<string, Guid?> _known = new(StringComparer.Ordinal);

    public async Task<Guid?> FindCompanyAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (_known.TryGetValue(userId, out var known))
        {
            return known;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var company = await CompanyOfAsync(db, userId, cancellationToken);
        _known[userId] = company;
        return company;
    }

    /// <summary>Forgets what is known about the user, after this scope registered them.</summary>
    public void Forget(string userId) => _known.Remove(userId);

    public static Task<Guid?> CompanyOfAsync(VendorsDbContext db, string userId, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<Guid?>($"select vendor.company_of_user({userId}) as \"Value\"").SingleAsync(cancellationToken);
}

/// <summary>The company of the current vendor context, under the company row-level security.</summary>
internal sealed class VendorCompanies(IDbContextFactory<VendorsDbContext> contexts, Platform.Shared.Tenancy.IVendorAccessor vendors) : IVendorCompanies
{
    public async Task<VendorCompany?> CurrentAsync(CancellationToken cancellationToken = default)
    {
        if (vendors.Current is not { } vendor)
        {
            return null;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var company = await db.Companies.AsNoTracking()
            .Where(c => c.Id == vendor.CompanyId)
            .Select(c => new { c.Id, c.CrNumber, c.NameAr, c.NameEn, c.VatNumber })
            .SingleOrDefaultAsync(cancellationToken);
        if (company is null)
        {
            return null;
        }

        // Under the tenant policy: only the current host tenant's row, none without a tenant.
        var status = await db.Relationships.AsNoTracking()
            .Where(r => r.CompanyId == vendor.CompanyId)
            .Select(r => r.Status)
            .SingleOrDefaultAsync(cancellationToken);
        return new VendorCompany(company.Id, company.CrNumber, company.NameAr, company.NameEn, company.VatNumber, Relationship(status));
    }

    private static VendorRelationshipStatus? Relationship(string? status) => status switch
    {
        null => null,
        "pending" => VendorRelationshipStatus.Pending,
        "approved" => VendorRelationshipStatus.Approved,
        _ => throw new InvalidOperationException($"Unknown relationship status '{status}'."),
    };
}
