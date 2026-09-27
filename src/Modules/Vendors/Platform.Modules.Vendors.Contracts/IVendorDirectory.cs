using Platform.Shared.Results;

namespace Platform.Modules.Vendors.Contracts;

/// <summary>
/// A vendor company related to the current tenant, as the tenant's list shows it (V-11): its names and CR number, where it
/// stands with the tenant (V-7), when the tenant first saw it, and the required documents that block a submission today
/// (Riyadh time), named in both languages.
/// </summary>
public sealed record RelatedVendor(
    Guid Id,
    string CrNumber,
    string NameAr,
    string NameEn,
    VendorRelationshipStatus Status,
    DateTimeOffset FirstSeenAt,
    IReadOnlyList<BlockingDocument> BlockingDocuments);

/// <summary>A document of a related vendor that scanned clean: the current file of its type or an older one (history).</summary>
public sealed record RelatedVendorDocument(Guid Id, string Type, DateOnly ExpiresOn, bool IsCurrent, DateTimeOffset UploadedAt);

/// <summary>
/// The company card of a related vendor (V-11): the company's shared facts, the tenant's relationship with it (status,
/// first seen, the approver's user id once approved), its clean documents newest first, and what blocks a submission today.
/// </summary>
public sealed record RelatedVendorDetails(
    Guid Id,
    string CrNumber,
    string NameAr,
    string NameEn,
    string VatNumber,
    string? Address,
    string ContactName,
    string? ContactPhone,
    string ContactEmail,
    VendorRelationshipStatus Status,
    DateTimeOffset FirstSeenAt,
    string? ApprovedBy,
    IReadOnlyList<RelatedVendorDocument> Documents,
    IReadOnlyList<BlockingDocument> BlockingDocuments);

/// <summary>
/// The current tenant's vendors, for its staff (vendor plan task 5, F-10 as narrowed, V-7, V-11). A company is visible only
/// while the tenant has a relationship with it; documents only once they scanned clean. The pages call it under the
/// VendorManager policy (a contracts officer or tenant admin); <see cref="ApproveAsync"/> checks the role again.
/// Throws <see cref="InvalidOperationException"/> when the scope has no tenant.
/// </summary>
public interface IVendorDirectory
{
    /// <summary>Every company related to the current tenant, pending and approved, by English name.</summary>
    Task<IReadOnlyList<RelatedVendor>> ListRelatedAsync(CancellationToken cancellationToken = default);

    /// <summary>The company's card while the current tenant has a relationship with it; null otherwise.</summary>
    Task<RelatedVendorDetails?> GetRelatedAsync(Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending company (V-7) as <paramref name="actorId"/>, who must be the acting user of the request or circuit
    /// (else <see cref="InvalidOperationException"/>) and hold the contracts officer or tenant admin role in the current
    /// tenant now (<see cref="VendorDirectoryErrors.NotAllowed"/>). Audited as <c>vendor.approved</c> in the tenant's log
    /// before the approval commits. Not related: <see cref="VendorDirectoryErrors.NotFound"/>; approved already:
    /// <see cref="VendorDirectoryErrors.AlreadyApproved"/>, and the first approver stays on record.
    /// </summary>
    Task<Result<VendorRelationshipStatus>> ApproveAsync(Guid companyId, string actorId, CancellationToken cancellationToken = default);
}

/// <summary>Stable error codes of <see cref="IVendorDirectory"/>; pages map them to localized text.</summary>
public static class VendorDirectoryErrors
{
    public const string NotFound = "vendor.not_related";
    public const string AlreadyApproved = "vendor.already_approved";
    public const string NotAllowed = "vendor.approve_not_allowed";
}

/// <summary>What joining a tenant changed: the relationship with it, and membership of its Keycloak organization.</summary>
public sealed record VendorJoined(bool RelationshipCreated, bool OrganizationAdded);

/// <summary>
/// A vendor of another tenant starts working with the tenant of the current host (vendor plan task 5, spec section 3,
/// ADR-0008): membership of the tenant's Keycloak organization and a pending relationship (V-7), audited as
/// <c>vendor.joined</c> in that tenant's log. The company is the current vendor context's, the user the acting user; the
/// host sets both after the JoiningVendor policy passed. Joining again changes nothing and is not audited again.
/// </summary>
public interface IVendorJoin
{
    /// <summary>
    /// Joins the host tenant. Keycloak failing: <see cref="VendorErrors.JoinFailed"/>, with nothing left behind. Throws
    /// <see cref="InvalidOperationException"/> without a tenant, a vendor context or an acting user of that company.
    /// </summary>
    Task<Result<VendorJoined>> JoinAsync(CancellationToken cancellationToken = default);
}
