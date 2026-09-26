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
}

/// <summary>The signed-in vendor's own company, as the vendor sees it.</summary>
public sealed record VendorCompany(Guid Id, string CrNumber, string NameAr, string NameEn, string VatNumber);

/// <summary>The company of the current vendor context (request or circuit), read under its row-level security.</summary>
public interface IVendorCompanies
{
    /// <summary>The company of the current vendor context, or null when there is none.</summary>
    Task<VendorCompany?> CurrentAsync(CancellationToken cancellationToken = default);
}
