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

    [Theory]
    [InlineData("{acme")]
    [InlineData("[acme")]
    [InlineData("[1]")]
    [InlineData("{\"acme\"")]
    public void A_malformed_json_claim_is_no_membership(string value)
    {
        var user = Principal(new Claim("organization", value));

        OrganizationClaims.Aliases(user).ShouldBeEmpty();
        OrganizationClaims.BelongsTo(user, Acme).ShouldBeFalse();
    }

    [Fact]
    public void A_non_string_array_element_is_skipped_and_the_string_elements_count()
    {
        var user = Principal(new Claim("organization", "[\"acme\", 5]"));

        OrganizationClaims.Aliases(user).ShouldBe(["acme"]);
        OrganizationClaims.BelongsTo(user, Acme).ShouldBeTrue();
    }

    [Fact]
    public void A_malformed_claim_does_not_hide_a_valid_one()
    {
        var user = Principal(new Claim("organization", "{acme"), new Claim("organization", "acme"));

        OrganizationClaims.BelongsTo(user, Acme).ShouldBeTrue();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));
}
