namespace Platform.Modules.Vendors.Contracts;

/// <summary>
/// Which vendor company a signed-in user belongs to, asked before any vendor context exists (by the Vendor policy, and by
/// the host infrastructure that sets the vendor context after the policy passed). Answers are kept for the request or
/// circuit.
/// </summary>
public interface IVendorUsers
{
    /// <summary>The company of the user with Keycloak <c>sub</c> <paramref name="userId"/>, or null when they have none.</summary>
    Task<Guid?> FindCompanyAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="FindCompanyAsync"/>, but always asks the database, never what this scope already knows, and never
    /// changes what it knows: an open circuit re-checks its vendor context with it from its revalidation loop, off the render
    /// thread, since an upheld dispute (W-33) can move the company away meanwhile.
    /// </summary>
    Task<Guid?> FindCurrentCompanyAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The signed-in vendor's own company, as the vendor sees it, with its relationship to the tenant of the current host
/// (V-7), or null when it has none there.
/// </summary>
public sealed record VendorCompany(
    Guid Id, string CrNumber, string NameAr, string NameEn, string VatNumber, VendorRelationshipStatus? Relationship);

/// <summary>Where a company stands with one tenant (V-7); <c>blocked</c> arrives with F-14.</summary>
public enum VendorRelationshipStatus
{
    /// <summary>First contact; a contracts officer or tenant admin has not approved the company yet.</summary>
    Pending,

    /// <summary>Approved by a contracts officer or tenant admin.</summary>
    Approved,
}

/// <summary>The company of the current vendor context (request or circuit), read under its row-level security.</summary>
public interface IVendorCompanies
{
    /// <summary>The company of the current vendor context, or null when there is none.</summary>
    Task<VendorCompany?> CurrentAsync(CancellationToken cancellationToken = default);
}
