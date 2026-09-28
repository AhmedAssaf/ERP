namespace Platform.Modules.Vendors.Contracts;

/// <summary>Authorization policy names for vendor pages (spec section 3); the web host registers them.</summary>
public static class VendorPolicies
{
    /// <summary>
    /// Signed in with a verified email, holding the Keycloak realm role <c>vendor</c>, with a <c>vendor.vendor_users</c>
    /// row, and a member of the host tenant's organization.
    /// </summary>
    public const string Vendor = "Vendor";

    /// <summary>Signed in with a verified email: who may open <c>/vendor/register/company</c>.</summary>
    public const string VendorApplicant = "VendorApplicant";

    /// <summary>
    /// The Vendor policy without the host check: a signed-in vendor with a verified email, the realm role <c>vendor</c> and
    /// a <c>vendor.vendor_users</c> row, whatever organizations it belongs to. Who may open <c>/vendor/join</c> on the
    /// host of a tenant it does not work with yet.
    /// </summary>
    public const string JoiningVendor = "JoiningVendor";

    /// <summary>
    /// Tenant staff who see and approve the tenant's vendors (V-11): a contracts officer or tenant admin of the host tenant,
    /// never a principal holding the realm role <c>vendor</c>.
    /// </summary>
    public const string VendorManager = "VendorManager";
}

/// <summary>
/// The platform's privacy notice shown at vendor registration (V-14, N-02). The text lives in the UI resources under
/// <c>Vendor.Privacy.{version}</c> in both languages; a new text is a new version, so what each vendor user accepted
/// stays known (a unit test pins the SHA-256 of every published text). The culture it was shown in is stored with the
/// acceptance.
/// </summary>
public static class VendorPrivacyNotice
{
    public const string CurrentVersion = "V1";

    public const string Arabic = "ar-SA";
    public const string English = "en-US";

    /// <summary>The cultures the notice is published in, as stored with an acceptance.</summary>
    public static IReadOnlyList<string> Cultures { get; } = [Arabic, English];
}
