using Microsoft.EntityFrameworkCore;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Documents;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Relationships;

/// <summary>
/// The current tenant's vendors for its staff (V-7, V-11). Every read goes through the security-definer functions that
/// answer only for companies related to <c>platform.current_tenant()</c> (<c>vendor.related_companies</c>,
/// <c>vendor.related_current_documents</c>, <c>vendor.related_company</c>, <c>vendor.related_documents</c>); the
/// relationship row itself is read under the tenant policy. Approval re-checks the actor's role in <c>identity.members</c>
/// at the moment it runs (the circuit's claims date from when the page opened), and the database records the acting user
/// of the session as the approver, never an argument. A scope with a vendor context is refused, as the functions refuse it.
/// </summary>
internal sealed class VendorDirectory(
    IDbContextFactory<VendorsDbContext> contexts,
    ITenantAccessor tenants,
    IVendorAccessor vendors,
    IActingUserAccessor actingUser,
    IMemberDirectory members,
    IAuditWriter audit,
    TimeProvider clock) : IVendorDirectory
{
    public async Task<IReadOnlyList<RelatedVendor>> ListRelatedAsync(CancellationToken cancellationToken = default)
    {
        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var companies = await db.Database.SqlQuery<CompanyListRow>($"""
            select id, cr_number, name_ar, name_en, status, first_seen_at
            from vendor.related_companies()
            """).ToListAsync(cancellationToken);
        var documents = (await db.Database.SqlQuery<CurrentDocumentRow>($"""
            select company_id, type, expires_on
            from vendor.related_current_documents()
            """).ToListAsync(cancellationToken))
            .ToLookup(d => d.CompanyId);
        var today = VendorCompliance.RiyadhToday(clock);

        return
        [
            .. companies
                .OrderBy(c => c.NameEn, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.CrNumber, StringComparer.Ordinal)
                .Select(c => new RelatedVendor(
                    c.Id, c.CrNumber, c.NameAr, c.NameEn, Status(c.Status), c.FirstSeenAt,
                    VendorCompliance.Blocking(documents[c.Id].Select(d => (d.Type, d.ExpiresOn)), today))),
        ];
    }

    public async Task<RelatedVendorDetails?> GetRelatedAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var relationship = await db.Relationships.AsNoTracking()
            .Where(r => r.CompanyId == companyId)
            .Select(r => new { r.Status, r.FirstSeenAt, r.ApprovedBy })
            .SingleOrDefaultAsync(cancellationToken);
        if (relationship is null)
        {
            return null;
        }

        var company = await db.Database.SqlQuery<CompanyCardRow>($"""
            select id, cr_number, name_ar, name_en, vat_number, address, contact_name, contact_phone, contact_email
            from vendor.related_company({companyId})
            """).SingleOrDefaultAsync(cancellationToken);
        if (company is null)
        {
            return null;
        }

        var documents = await db.Database.SqlQuery<DocumentListRow>($"""
            select id, type, expires_on, is_current, created_at
            from vendor.related_documents({companyId})
            order by created_at desc
            """).ToListAsync(cancellationToken);
        var blocking = VendorCompliance.Blocking(
            documents.Where(d => d.IsCurrent).Select(d => (d.Type, d.ExpiresOn)), VendorCompliance.RiyadhToday(clock));

        return new RelatedVendorDetails(
            company.Id, company.CrNumber, company.NameAr, company.NameEn, company.VatNumber, company.Address,
            company.ContactName, company.ContactPhone, company.ContactEmail,
            Status(relationship.Status), relationship.FirstSeenAt, relationship.ApprovedBy,
            [.. documents.Select(d => new RelatedVendorDocument(d.Id, d.Type, d.ExpiresOn, d.IsCurrent, d.CreatedAt))],
            blocking);
    }

    public async Task<Result<VendorRelationshipStatus>> ApproveAsync(Guid companyId, string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        RequireTenant();
        if (!string.Equals(actingUser.UserId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A vendor is approved by the acting user of the request or circuit.");
        }

        var roles = await members.GetRolesAsync(actorId, cancellationToken);
        if (!roles.Contains(TenantRoles.ContractsOfficer, StringComparer.Ordinal) && !roles.Contains(TenantRoles.TenantAdmin, StringComparer.Ordinal))
        {
            return Result.Failure<VendorRelationshipStatus>(Error.Refused(
                VendorDirectoryErrors.NotAllowed, "Only a contracts officer or a tenant administrator can approve a vendor."));
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var company = await db.Database.SqlQuery<CompanyCardRow>($"""
            select id, cr_number, name_ar, name_en, vat_number, address, contact_name, contact_phone, contact_email
            from vendor.related_company({companyId})
            """).SingleOrDefaultAsync(cancellationToken);
        if (company is null)
        {
            return NotFound();
        }

        // Holds the relationship row until the commit, so a second approval waits and then finds it approved.
        if (!await db.Database.SqlQuery<bool>($"select vendor.approve_relationship({companyId}) as \"Value\"").SingleAsync(cancellationToken))
        {
            return Result.Failure<VendorRelationshipStatus>(Error.Conflict(
                VendorDirectoryErrors.AlreadyApproved, "This vendor is already approved."));
        }

        // Before the commit, so an approval is never recorded without its audit entry: when the audit fails the approval
        // rolls back. A commit that fails after it leaves an entry for an approval that did not happen; approving again
        // then succeeds and writes a second entry.
        await audit.WriteAsync(
            new AuditEntry(actorId, "vendor.approved", "vendor_company", companyId.ToString(), new Dictionary<string, string?>
            {
                ["cr_number"] = company.CrNumber,
                ["name_en"] = company.NameEn,
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(VendorRelationshipStatus.Approved);
    }

    // Tenant staff only: a vendor on a tenant host must never read other companies related to that tenant. The database
    // functions refuse a vendor context too (migration 0011); this says so before any query.
    private void RequireTenant()
    {
        if (tenants.Current is null)
        {
            throw new InvalidOperationException("The vendor directory is read by tenant staff on a tenant host; this scope has none.");
        }

        if (vendors.Current is not null)
        {
            throw new InvalidOperationException("The vendor directory is tenant staff's; a scope with a vendor context may not use it.");
        }
    }

    private static Result<VendorRelationshipStatus> NotFound() =>
        Result.Failure<VendorRelationshipStatus>(Error.NotFound(VendorDirectoryErrors.NotFound, "This vendor does not work with this organization."));

    private static VendorRelationshipStatus Status(string status) => status switch
    {
        "pending" => VendorRelationshipStatus.Pending,
        "approved" => VendorRelationshipStatus.Approved,
        _ => throw new InvalidOperationException($"Unknown relationship status '{status}'."),
    };

    // Rows of the functions' result sets; columns map by the snake_case convention.
    private sealed class CompanyListRow
    {
        public Guid Id { get; set; }

        public string CrNumber { get; set; } = string.Empty;

        public string NameAr { get; set; } = string.Empty;

        public string NameEn { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public DateTimeOffset FirstSeenAt { get; set; }
    }

    private sealed class CurrentDocumentRow
    {
        public Guid CompanyId { get; set; }

        public string Type { get; set; } = string.Empty;

        public DateOnly ExpiresOn { get; set; }
    }

    private sealed class CompanyCardRow
    {
        public Guid Id { get; set; }

        public string CrNumber { get; set; } = string.Empty;

        public string NameAr { get; set; } = string.Empty;

        public string NameEn { get; set; } = string.Empty;

        public string VatNumber { get; set; } = string.Empty;

        public string? Address { get; set; }

        public string ContactName { get; set; } = string.Empty;

        public string? ContactPhone { get; set; }

        public string ContactEmail { get; set; } = string.Empty;
    }

    private sealed class DocumentListRow
    {
        public Guid Id { get; set; }

        public string Type { get; set; } = string.Empty;

        public DateOnly ExpiresOn { get; set; }

        public bool IsCurrent { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
