using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Results;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// Vendor registration input (F-11, V-6, V-14; vendor plan task 2) through <see cref="IVendorRegistration"/> on the test
/// database. Every refusal here happens before the Keycloak Admin API is called, so the Admin API points at a port where
/// nothing listens: a test that reached it would fail with a connection error instead of the expected code.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorRegistrationInputTests(DatabaseFixture db)
{
    /// <summary>V-6, word for word.</summary>
    private const string DuplicateMessage = "This company already has an account on WaslaBid. Ask its administrator to add you.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("123456789")]
    [InlineData("12345678901")]
    [InlineData("12345abcde")]
    [InlineData("1234 56789")]
    [InlineData("")]
    public async Task A_cr_number_that_is_not_ten_digits_is_refused_with_its_own_code(string cr)
    {
        var result = await RegisterAsync(Valid() with { CrNumber = cr });

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.InvalidCrNumber);
    }

    [Theory]
    [InlineData("300000000000002")]
    [InlineData("200000000000003")]
    [InlineData("30000000000003")]
    [InlineData("3000000000000033")]
    [InlineData("30000000000a003")]
    [InlineData(null)]
    public async Task A_vat_number_with_the_wrong_shape_is_refused(string? vat)
    {
        var result = await RegisterAsync(Valid() with { VatNumber = vat });

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.InvalidVatNumber);
    }

    [Fact]
    public async Task Validation_reports_every_field_that_is_wrong_at_once()
    {
        await using var host = Host();
        await using var scope = host.ScopeFor(TestTenants.Acme);
        var registration = scope.ServiceProvider.GetRequiredService<IVendorRegistration>();

        var errors = registration.Validate(new VendorRegistration(
            "12", "‮شركة", "<b>Co</b>", "1", new string('a', 501), "", "call me", "not-an-email", null));

        errors.Select(e => e.Code).ShouldBe(
        [
            VendorErrors.InvalidCrNumber, VendorErrors.InvalidNameAr, VendorErrors.InvalidNameEn, VendorErrors.InvalidVatNumber,
            VendorErrors.InvalidAddress, VendorErrors.InvalidContactName, VendorErrors.InvalidContactPhone,
            VendorErrors.InvalidContactEmail, VendorErrors.PrivacyNoticeRequired,
        ]);
        registration.Validate(Valid()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("​شركة الأفق")]
    [InlineData("Al Ufuq‮ Co")]
    [InlineData("<script>")]
    public async Task A_company_name_with_markup_or_invisible_characters_is_refused(string name)
    {
        (await RegisterAsync(Valid() with { NameEn = name })).Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.InvalidNameEn);
        (await RegisterAsync(Valid() with { NameAr = name })).Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.InvalidNameAr);
    }

    [Theory]
    [InlineData("contact@localhost")]
    [InlineData("contact@192.168.1.1")]
    [InlineData("\"quoted\"@example.com")]
    [InlineData("اتصال@example.com")]
    public async Task A_contact_email_is_held_to_the_staff_email_rule(string email)
    {
        (await RegisterAsync(Valid() with { ContactEmail = email })).Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.InvalidContactEmail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("V0")]
    [InlineData("")]
    public async Task Registration_without_accepting_the_current_privacy_notice_is_refused(string? accepted)
    {
        var userId = NewUserId();

        var result = await RegisterAsync(Valid() with { AcceptedPrivacyNotice = accepted }, userId);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.PrivacyNoticeRequired);
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_second_registration_with_the_same_cr_is_refused_without_revealing_the_company()
    {
        var cr = VendorRows.NewCrNumber();
        const string existingName = "Existing Holder Trading";
        await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Beta, NewUserId(), cr, existingName, Ct);
        var userId = NewUserId();

        var result = await RegisterAsync(Valid() with { CrNumber = cr }, userId);

        var error = result.Error.ShouldNotBeNull();
        error.Code.ShouldBe(VendorErrors.DuplicateCr);
        error.Message.ShouldBe(DuplicateMessage);
        error.Message.ShouldNotContain(existingName);
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldBeNull();
        (await VendorRows.CompaniesWithCrAsync(db.OwnerConnectionString, cr, Ct)).ShouldBe(1);
        var audit = (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, userId, "vendor.duplicate_cr_refused", Ct))
            .ShouldHaveSingleItem();
        audit.SubjectId.ShouldBe(cr);
        audit.Data.ShouldNotContain(existingName);
        // The company's own tenant learns nothing about the attempt through its log.
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.duplicate_cr_refused", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_staff_member_of_the_host_tenant_cannot_register_a_company()
    {
        var userId = NewUserId();
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, userId, $"{userId}@acme.test", [TenantRoles.ContractsOfficer], "active", Ct);

        var result = await RegisterAsync(Valid(), userId);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.StaffAccount);
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_user_who_already_has_a_company_cannot_register_another()
    {
        var userId = NewUserId();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Beta, userId, VendorRows.NewCrNumber(), "First Company", Ct);

        var result = await RegisterAsync(Valid(), userId);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.AlreadyRegistered);
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldNotBeNull().CompanyId.ShouldBe(companyId);
    }

    [Fact]
    public async Task Arabic_indic_digits_in_the_cr_and_vat_numbers_are_accepted_as_their_ascii_digits()
    {
        await using var host = Host();
        await using var scope = host.ScopeFor(TestTenants.Acme);
        var registration = scope.ServiceProvider.GetRequiredService<IVendorRegistration>();

        registration.Validate(Valid() with { CrNumber = "١٠١٠١٢٣٤٥٦", VatNumber = "۳۰۰۰۰۰۰۰۰۰۰۰۰۰۳" }).ShouldBeEmpty();
    }

    internal static VendorRegistration Valid(string? cr = null) => new(
        cr ?? VendorRows.NewCrNumber(), "شركة الأفق للتجارة", "Al Ufuq Trading Co. 2", "300000000000003", "Riyadh, King Fahd Road",
        "Sara Al-Ahmed", "+966 50 000 0000", "sara@alufuq.example", VendorPrivacyNotice.CurrentVersion);

    private async Task<Result<Guid>> RegisterAsync(VendorRegistration input, string? userId = null)
    {
        await using var host = Host();
        await using var scope = host.ScopeFor(TestTenants.Acme);
        return await scope.ServiceProvider.GetRequiredService<IVendorRegistration>()
            .RegisterCompanyAsync(input, userId ?? NewUserId(), "applicant@example.test", Ct);
    }

    private ModuleHost Host() => new(db.AppConnectionString, UnreachableKeycloak());

    private static string NewUserId() => Guid.NewGuid().ToString();

    // Nothing listens on the discard port, so any Admin API call fails to connect.
    private static IConfiguration UnreachableKeycloak() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeycloakAdmin:BaseUrl"] = "http://127.0.0.1:9",
            ["KeycloakAdmin:ClientSecret"] = "unused",
            ["KeycloakAdmin:TenantUrl"] = "https://{slug}.localhost:8443/",
        })
        .Build();
}
