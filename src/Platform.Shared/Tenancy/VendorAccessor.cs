namespace Platform.Shared.Tenancy;

/// <summary>
/// The vendor company a signed-in vendor user acts for (vendor spec section 2, ADR-0008). The connection interceptor
/// writes it to <c>app.vendor_company_id</c>, which the vendor tables' row-level security reads.
/// </summary>
public sealed record VendorContext(Guid CompanyId);

public interface IVendorAccessor
{
    VendorContext? Current { get; }
}

/// <summary>
/// Scoped holder set once per request or circuit, like <see cref="TenantAccessor"/>. Only host infrastructure (the
/// vendor middleware after the Vendor policy passes, tests) may call <see cref="Set"/>; application code depends on
/// <see cref="IVendorAccessor"/> and only reads <see cref="Current"/>.
/// </summary>
public sealed class VendorAccessor : IVendorAccessor
{
    public VendorContext? Current { get; private set; }

    /// <summary>Sets the company once. Setting the same company again is a no-op; setting a different one throws.</summary>
    public void Set(VendorContext vendor)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        if (vendor.CompanyId == Guid.Empty)
        {
            throw new ArgumentException("The vendor company id must not be empty.", nameof(vendor));
        }

        if (Current is not null && Current != vendor)
        {
            throw new InvalidOperationException(
                "The vendor company is already set; it cannot be changed within the same request or circuit.");
        }

        Current = vendor;
    }
}
