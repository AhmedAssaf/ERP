using System.Security.Claims;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Web.Usage;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// Spec 6.2 (O-24): who counts in the usage metrics, and as what. One classifier for the circuit registry and the
/// activity recorder. A pure function, so no database; it lives here because the unit test project does not reference
/// the web host.
/// </summary>
public sealed class UsageKindTests
{
    private static readonly Guid Company = Guid.NewGuid();

    public static TheoryData<string, string?> Sessions => new()
    {
        { "staff of the host tenant with a tenant role", "Staff" },
        { "staff of the host tenant with two roles", "Staff" },
        { "organization member without a tenant role", null },
        { "a role claim on the token's own identity only", null },
        { "staff of another tenant presenting its session here", null },
        { "staff holding the realm role vendor", null },
        { "vendor under the Vendor policy", "Vendor" },
        { "vendor on the join page of a tenant it has not joined", null },
        { "applicant before its company exists", null },
        { "platform admin with OTP on the platform host", "Platform" },
        { "platform admin without OTP", null },
        { "tenant admin session on the platform host", null },
        { "platform admin session on a tenant host", null },
        { "anonymous on a tenant host", null },
        { "anonymous on the platform host", null },
    };

    [Theory]
    [MemberData(nameof(Sessions))]
    public void The_usage_kind_of_each_session(string session, string? expected)
    {
        var s = Build(session);

        var kind = UsageKinds.Of(s.User, s.Tenants, s.Vendor, s.Platform);

        kind?.ToString().ShouldBe(expected, session);
        (kind is null).ShouldBe(expected is null, session);
    }

    [Fact]
    public void Kinds_carry_the_fixed_tag_values_of_spec_6_1()
    {
        UsageKinds.TagValue(UsageKind.Staff).ShouldBe("staff");
        UsageKinds.TagValue(UsageKind.Vendor).ShouldBe("vendor");
        UsageKinds.TagValue(UsageKind.Platform).ShouldBe("platform");
    }

    private static UsageSession Build(string session)
    {
        var acme = TestTenants.Acme;
        switch (session)
        {
            case "staff of the host tenant with a tenant role":
                return UsageSession.Staff("staff-1", acme);
            case "staff of the host tenant with two roles":
                return UsageSession.Staff("staff-2", acme, TenantRoles.ContractsOfficer, TenantRoles.FinanceApprover);
            case "organization member without a tenant role":
                return UsageSession.OnTenant(UsageSession.Principal("member", ["acme"], [], memberRoles: []), acme);
            case "a role claim on the token's own identity only":
                var token = UsageSession.Principal("token-roles", ["acme"], []);
                ((ClaimsIdentity)token.Identity!).AddClaim(new Claim(IdentityClaims.Role, TenantRoles.TenantAdmin));
                return UsageSession.OnTenant(token, acme);
            case "staff of another tenant presenting its session here":
                return UsageSession.OnTenant(UsageSession.Principal("beta-staff", ["beta"], [], [TenantRoles.TenantAdmin]), acme);
            case "staff holding the realm role vendor":
                return UsageSession.OnTenant(UsageSession.Principal("both", ["acme"], [IdentityClaims.VendorRealmRole], [TenantRoles.TenantAdmin]), acme);
            case "vendor under the Vendor policy":
                return UsageSession.VendorUser("vendor-1", acme, Company);
            case "vendor on the join page of a tenant it has not joined":
                // The JoiningVendor policy sets the vendor context without the host tenant's organization.
                var joining = UsageSession.OnTenant(UsageSession.Principal("joining", ["beta"], [IdentityClaims.VendorRealmRole]), acme);
                joining.Vendor.Set(new Platform.Shared.Tenancy.VendorContext(Company));
                return joining;
            case "applicant before its company exists":
                return UsageSession.OnTenant(UsageSession.Principal("applicant", ["acme"], [IdentityClaims.VendorRealmRole]), acme);
            case "platform admin with OTP on the platform host":
                return UsageSession.PlatformAdmin("platform-1");
            case "platform admin without OTP":
                return UsageSession.PlatformAdmin("platform-2", acr: "1");
            case "tenant admin session on the platform host":
                var tenantAdmin = new UsageSession(
                    UsageSession.Principal("acme.admin", ["acme"], [], [TenantRoles.TenantAdmin], acr: "2"),
                    new Platform.Shared.Tenancy.TenantAccessor(), new Platform.Shared.Tenancy.VendorAccessor(), new Platform.Shared.Tenancy.PlatformRequestContext());
                tenantAdmin.Platform.MarkPlatform();
                return tenantAdmin;
            case "platform admin session on a tenant host":
                return UsageSession.OnTenant(UsageSession.Principal("platform-3", [], ["platform-admin"], acr: "2"), acme);
            case "anonymous on a tenant host":
                return UsageSession.Anonymous(acme);
            case "anonymous on the platform host":
                var anonymous = new UsageSession(
                    new ClaimsPrincipal(new ClaimsIdentity()), new Platform.Shared.Tenancy.TenantAccessor(),
                    new Platform.Shared.Tenancy.VendorAccessor(), new Platform.Shared.Tenancy.PlatformRequestContext());
                anonymous.Platform.MarkPlatform();
                return anonymous;
            default:
                throw new ArgumentOutOfRangeException(nameof(session), session, null);
        }
    }
}
