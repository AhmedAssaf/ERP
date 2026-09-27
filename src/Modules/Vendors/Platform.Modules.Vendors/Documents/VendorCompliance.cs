using Microsoft.EntityFrameworkCore;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Documents;

/// <summary>
/// Which required documents block a vendor company's submission (spec section 3). As the company itself (its vendor
/// context) the documents are read under the company policy; as tenant staff (a tenant and no vendor context) only
/// through <c>vendor.related_documents</c>, which answers only while the tenant has a relationship with the company and
/// only with files that scanned clean. Another company's vendor context is refused, whatever the host.
/// </summary>
internal sealed class VendorCompliance(
    IDbContextFactory<VendorsDbContext> contexts, IVendorAccessor vendors, ITenantAccessor tenants) : IVendorCompliance
{
    public async Task<IReadOnlyList<BlockingDocument>> GetBlockingDocumentsAsync(
        Guid companyId, DateOnly onDate, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        List<CurrentDocument> current;
        if (vendors.Current is { } vendor)
        {
            if (vendor.CompanyId != companyId)
            {
                throw new InvalidOperationException("A vendor asks about its own company's documents only.");
            }

            current = await db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.IsCurrent && d.ScanStatus == "clean")
                .Select(d => new CurrentDocument { Type = d.Type, ExpiresOn = d.ExpiresOn })
                .ToListAsync(cancellationToken);
        }
        else if (tenants.Current is not null)
        {
            current = await db.Database.SqlQuery<CurrentDocument>($"""
                select type, expires_on
                from vendor.related_documents({companyId})
                where is_current
                """).ToListAsync(cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("Document compliance is asked by the company's vendor or by tenant staff.");
        }

        return Blocking(current.Select(d => (d.Type, d.ExpiresOn)), onDate);
    }

    /// <summary>
    /// Every required type without a current clean file among <paramref name="current"/>, or whose file expired before
    /// <paramref name="onDate"/> (a file is valid through its expiry date), in the order of <see cref="VendorDocumentTypes.All"/>.
    /// </summary>
    internal static IReadOnlyList<BlockingDocument> Blocking(IEnumerable<(string Type, DateOnly ExpiresOn)> current, DateOnly onDate)
    {
        var byType = current.ToDictionary(d => d.Type, d => d.ExpiresOn, StringComparer.Ordinal);
        var blocking = new List<BlockingDocument>();
        foreach (var type in VendorDocumentTypes.All)
        {
            if (!byType.TryGetValue(type.Code, out var expiresOn))
            {
                blocking.Add(new BlockingDocument(type.Code, type.NameAr, type.NameEn, BlockingReason.Missing, ExpiredOn: null));
            }
            else if (expiresOn < onDate)
            {
                blocking.Add(new BlockingDocument(type.Code, type.NameAr, type.NameEn, BlockingReason.Expired, expiresOn));
            }
        }

        return blocking;
    }

    /// <summary>Today in Riyadh (UTC+3 all year, no daylight saving): the date documents are checked against.</summary>
    internal static DateOnly RiyadhToday(TimeProvider clock) =>
        DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(3)).DateTime);

    /// <summary>The current clean file of a type and its expiry (columns type and expires_on, by the snake_case convention).</summary>
    private sealed class CurrentDocument
    {
        public string Type { get; set; } = string.Empty;

        public DateOnly ExpiresOn { get; set; }
    }
}
