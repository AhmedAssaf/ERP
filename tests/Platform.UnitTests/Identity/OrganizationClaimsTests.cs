using System.Security.Claims;
using Platform.Modules.Identity;
using Platform.Shared.Tenancy;

namespace Platform.UnitTests.Identity;

public class OrganizationClaimsTests
{
    private static readonly TenantContext Acme = new(Guid.NewGuid(), "acme", "acme", "ar-SA", new TenantBranding("Acme", "#0F766E", null));

    [Fact]
    public void One_claim_per_alias_is_read_as_membership()
    {
        var user = Principal(new Claim("organization", "acme"), new Claim("organization", "beta"));

        OrganizationClaims.Aliases(user).ShouldBe(["acme", "beta"], ignoreOrder: true);
        OrganizationClaims.BelongsTo(user, Acme).ShouldBeTrue();
    }

    [Fact]
    public void A_json_array_claim_is_read_as_membership() =>
        OrganizationClaims.BelongsTo(Principal(new Claim("organization", "[\"acme\"]")), Acme).ShouldBeTrue();

    [Fact]
    public void A_json_object_claim_with_ids_is_read_by_alias()
    {
        var user = Principal(new Claim("organization", "{\"acme\":{\"id\":\"d9df78c6-c1ac-404c-9462-d8aae572c603\"}}"));

        OrganizationClaims.BelongsTo(user, Acme).ShouldBeTrue();
    }

    [Fact]
    public void Another_organization_does_not_belong() =>
        OrganizationClaims.BelongsTo(Principal(new Claim("organization", "beta")), Acme).ShouldBeFalse();

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));
}
