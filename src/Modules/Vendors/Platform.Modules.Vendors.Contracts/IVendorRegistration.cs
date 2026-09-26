using Platform.Shared.Results;

namespace Platform.Modules.Vendors.Contracts;

/// <summary>
/// What a vendor types on <c>/vendor/register/company</c> (F-11, spec section 3). <see cref="AcceptedPrivacyNotice"/> is
/// the version of the privacy notice the form showed and the person accepted (V-14); null when the box was not ticked.
/// <see cref="PrivacyNoticeCulture"/> is the culture the form showed that notice in (one of
/// <see cref="VendorPrivacyNotice.Cultures"/>); null takes the culture of the request. The signed-in user and their
/// verified email are never part of it: they come from the principal.
/// </summary>
public sealed record VendorRegistration(
    string? CrNumber,
    string? NameAr,
    string? NameEn,
    string? VatNumber,
    string? Address,
    string? ContactName,
    string? ContactPhone,
    string? ContactEmail,
    string? AcceptedPrivacyNotice,
    string? PrivacyNoticeCulture = null);

/// <summary>Whether the signed-in user may register a company on this tenant's host, as the registration page asks first.</summary>
public enum VendorRegistrationCheck
{
    /// <summary>Nothing known stands in the way (the registration itself checks everything again).</summary>
    Open,

    /// <summary>The user already belongs to a vendor company.</summary>
    AlreadyRegistered,

    /// <summary>The account is tenant staff: a member row of this tenant, or a member of an organization as staff.</summary>
    StaffAccount,

    /// <summary>The identity provider did not answer; the page shows the form and the registration decides.</summary>
    Unknown,
}

/// <summary>
/// Vendor self-registration through a tenant host (F-11, V-3 to V-7, V-14). Works on the tenant of the current request.
/// Error codes are in <see cref="VendorErrors"/>.
/// </summary>
public interface IVendorRegistration
{
    /// <summary>
    /// Every field error of <paramref name="registration"/> in form order (CR, Arabic name, English name, VAT, address,
    /// contact name, phone, email, privacy notice), so a form can show them all at once; empty when the input is valid.
    /// Nothing is looked up or stored.
    /// </summary>
    IReadOnlyList<Error> Validate(VendorRegistration registration);

    /// <summary>
    /// The refusals <see cref="RegisterCompanyAsync"/> would give the acting user before any input is read (a company
    /// already, tenant staff, another organization), so the page can say so before the form. Changes nothing.
    /// </summary>
    Task<VendorRegistrationCheck> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers the company with the acting user of the request or circuit (the signed-in principal's Keycloak
    /// <c>sub</c>, set by the host; never a caller's argument) as its first vendor admin and a pending relationship with
    /// the host tenant, grants the Keycloak realm role <c>vendor</c> and membership of the tenant's organization, and
    /// audits <c>vendor.registered</c> in the tenant's log. <paramref name="email"/> is the user's verified email from the
    /// token, kept in the audit entry. A CR number already on the platform is refused with one neutral message and audited
    /// as <c>vendor.duplicate_cr_refused</c> in the platform audit under the number's SHA-256 (V-6); after five such
    /// refusals in an hour every CR number gets that answer for the user. An account left half registered (the role and
    /// only this tenant's organization, no company) may finish. Returns the company id. Throws <see cref="InvalidOperationException"/>
    /// when the scope has no tenant or no acting user.
    /// </summary>
    Task<Result<Guid>> RegisterCompanyAsync(VendorRegistration registration, string email, CancellationToken cancellationToken = default);
}

/// <summary>Stable error codes of the vendor services; pages map them to localized text.</summary>
public static class VendorErrors
{
    public const string InvalidCrNumber = "vendor.invalid_cr_number";
    public const string InvalidVatNumber = "vendor.invalid_vat_number";
    public const string InvalidNameAr = "vendor.invalid_name_ar";
    public const string InvalidNameEn = "vendor.invalid_name_en";
    public const string InvalidAddress = "vendor.invalid_address";
    public const string InvalidContactName = "vendor.invalid_contact_name";
    public const string InvalidContactPhone = "vendor.invalid_contact_phone";
    public const string InvalidContactEmail = "vendor.invalid_contact_email";
    public const string PrivacyNoticeRequired = "vendor.privacy_notice_required";

    /// <summary>V-6: the CR number is already on WaslaBid. The message never names the company.</summary>
    public const string DuplicateCr = "vendor.duplicate_cr";

    /// <summary>The signed-in user already belongs to a vendor company.</summary>
    public const string AlreadyRegistered = "vendor.already_registered";

    /// <summary>The account is tenant staff (a member row, or membership of an organization): it cannot be a vendor (V-3).</summary>
    public const string StaffAccount = "vendor.staff_account";

    public const string RegistrationFailed = "vendor.registration_failed";
}
