using Platform.Shared.Tenancy;

namespace Platform.UnitTests.Shared;

public class TenantAccessorTests
{
    private static TenantContext MakeTenant(string slug = "acme") => new(
        Guid.NewGuid(), slug, slug, "en-US", new TenantBranding("Acme", "#000000", null));

    [Fact]
    public void Set_stores_the_tenant()
    {
        var accessor = new TenantAccessor();
        var tenant = MakeTenant();

        accessor.Set(tenant);

        accessor.Current.ShouldBe(tenant);
    }

    [Fact]
    public void Setting_the_same_tenant_again_is_a_no_op()
    {
        var accessor = new TenantAccessor();
        var tenant = MakeTenant();
        accessor.Set(tenant);

        accessor.Set(tenant);

        accessor.Current.ShouldBe(tenant);
    }

    [Fact]
    public void Setting_a_different_tenant_throws()
    {
        var accessor = new TenantAccessor();
        accessor.Set(MakeTenant("acme"));

        Should.Throw<InvalidOperationException>(() => accessor.Set(MakeTenant("beta")));
    }

    [Fact]
    public void Setting_null_throws()
    {
        var accessor = new TenantAccessor();

        Should.Throw<ArgumentNullException>(() => accessor.Set(null!));
    }
}
