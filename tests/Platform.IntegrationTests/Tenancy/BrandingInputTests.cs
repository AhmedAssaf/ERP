using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Tenancy;

/// <summary>
/// QA pass, F-02 negative inputs through <see cref="IBrandingService"/>: colours outside <c>#RRGGBB</c> and portal names
/// the service must refuse leave the tenant's branding unchanged; accepted edge forms are normalised.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BrandingInputTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>
{
    private const string Actor = "qa-branding-admin";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("0F766E")]
    [InlineData("#0F766")]
    [InlineData("#0F766E0")]
    [InlineData("#0F766E00")]
    [InlineData("#GGGGGG")]
    [InlineData("#0F7 66E")]
    [InlineData("#０F766E")]
    [InlineData("rgb(15,118,110)")]
    [InlineData("#0F766E\u0000")]
    [InlineData("#0F766E\u202E")]
    [InlineData("#fff")]
    [InlineData("")]
    public async Task A_colour_outside_hash_and_six_hex_digits_is_refused_and_nothing_changes(string colour)
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);

        var saved = await SaveAsync(tenant, "Qa Portal", colour);

        saved.IsSuccess.ShouldBeFalse();
        saved.Error.Code.ShouldBe(BrandingErrors.InvalidColor);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).ShouldBe(tenant.Branding);
    }

    [Fact]
    public async Task A_lower_case_colour_with_surrounding_spaces_is_stored_upper_case()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);

        var saved = await SaveAsync(tenant, "Qa Portal", "  #1e4e79 ");

        saved.IsSuccess.ShouldBeTrue();
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).PrimaryColor.ShouldBe("#1E4E79");
    }

    [Fact]
    public async Task An_arabic_portal_name_is_stored_as_given()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);

        var saved = await SaveAsync(tenant, "بوابة مناقصات شركة الراجحي للمقاولات", "#0F766E");

        saved.IsSuccess.ShouldBeTrue();
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).PortalName.ShouldBe("بوابة مناقصات شركة الراجحي للمقاولات");
    }

    [Theory]
    [InlineData("Portal\u0000Name")]
    [InlineData("Portal\nName")]
    [InlineData("Portal\u0085Name")]
    public async Task A_portal_name_with_a_control_character_is_refused(string name)
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);

        var saved = await SaveAsync(tenant, name, "#0F766E");

        saved.Error!.Code.ShouldBe(BrandingErrors.InvalidPortalName);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).ShouldBe(tenant.Branding);
    }

    [Theory]
    [InlineData("Acme\u202E Portal")]
    [InlineData("Acme\u200B Portal")]
    [InlineData("\u2066Acme Portal\u2069")]
    [InlineData("\uFEFFAcme Portal")]
    public async Task A_portal_name_with_a_bidi_override_or_zero_width_character_is_refused(string name)
    {
        // The portal name is shown to every vendor on the tenant's pages and goes into invitation emails (F-02, F-06);
        // the characters the display-name rule refuses for the same reason must not enter through the portal name.
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);

        var saved = await SaveAsync(tenant, name, "#0F766E");

        saved.IsSuccess.ShouldBeFalse("a portal name with a bidi-override or zero-width character was stored");
        saved.Error.Code.ShouldBe(BrandingErrors.InvalidPortalName);
    }

    private async Task<Result<BrandingSaved>> SaveAsync(TenantContext tenant, string name, string colour)
    {
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, tenant.TenantId, Actor, Ct);
        await using var host = new ModuleHost(db.AppConnectionString, objectStorage: new ConfigurationBuilder().AddInMemoryCollection(minio.Settings).Build());
        await using var scope = host.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IBrandingService>().SaveAsync(name, colour, Actor, Ct);
    }
}
