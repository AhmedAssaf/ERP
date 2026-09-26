using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

internal static class TestTenants
{
    public static TenantContext Acme { get; } = new(
        Guid.Parse("0f0e0d0c-0000-7000-8000-00000000ac01"), "acme", "acme", "ar-SA",
        new TenantBranding("Acme Contracting", "#0F766E", null));

    public static TenantContext Beta { get; } = new(
        Guid.Parse("0f0e0d0c-0000-7000-8000-00000000be01"), "beta", "beta", "en-US",
        new TenantBranding("Beta Industries", "#9A3412", null));
}
