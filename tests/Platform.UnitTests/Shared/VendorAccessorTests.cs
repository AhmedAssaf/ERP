using Platform.Shared.Tenancy;

namespace Platform.UnitTests.Shared;

public class VendorAccessorTests
{
    [Fact]
    public void Set_stores_the_vendor_company()
    {
        var accessor = new VendorAccessor();
        var vendor = new VendorContext(Guid.NewGuid());

        accessor.Set(vendor);

        accessor.Current.ShouldBe(vendor);
    }

    [Fact]
    public void Setting_the_same_company_again_is_a_no_op()
    {
        var accessor = new VendorAccessor();
        var companyId = Guid.NewGuid();
        accessor.Set(new VendorContext(companyId));

        accessor.Set(new VendorContext(companyId));

        accessor.Current.ShouldBe(new VendorContext(companyId));
    }

    [Fact]
    public void Setting_a_different_company_throws()
    {
        var accessor = new VendorAccessor();
        accessor.Set(new VendorContext(Guid.NewGuid()));

        Should.Throw<InvalidOperationException>(() => accessor.Set(new VendorContext(Guid.NewGuid())));
    }

    [Fact]
    public void Setting_null_throws()
    {
        var accessor = new VendorAccessor();

        Should.Throw<ArgumentNullException>(() => accessor.Set(null!));
    }

    [Fact]
    public void An_empty_company_id_is_refused()
    {
        var accessor = new VendorAccessor();

        Should.Throw<ArgumentException>(() => accessor.Set(new VendorContext(Guid.Empty)));
    }
}
