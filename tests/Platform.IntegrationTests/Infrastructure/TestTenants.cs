using Platform.Migrator;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

internal static class TestTenants
{
    public static TenantContext Acme { get; } = DevSeed.Acme.ToContext();

    public static TenantContext Beta { get; } = DevSeed.Beta.ToContext();
}
