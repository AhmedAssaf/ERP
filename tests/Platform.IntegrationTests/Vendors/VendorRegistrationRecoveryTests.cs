using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Results;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// Vendor registration when a step fails or an earlier attempt stopped half way (vendor plan task 2 review): Keycloak is a
/// <see cref="FakeVendorAccounts"/> and the database the test database, so each failure can be forced. What Keycloak was
/// given is taken back only when, after the failure, the user still has no company; an account that holds the vendor role
/// and only the host tenant's organization but no company may finish its registration; the precheck the registration page
/// runs gives the same answers as the registration itself; and the culture the privacy notice was shown in is stored.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorRegistrationRecoveryTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_database_failure_after_the_grant_takes_back_what_keycloak_was_given()
    {
        var accounts = new FakeVendorAccounts();
        var userId = NewUserId();

        var result = await RegisterAsync(accounts, userId, VendorRegistrationInputTests.Valid(), failRegistration: true);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.RegistrationFailed);
        accounts.Revoked.ShouldHaveSingleItem().ShouldBe(new VendorAccessGrant(userId, "acme", RoleAdded: true, OrganizationAdded: true));
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Failing_to_add_the_organization_after_granting_the_role_takes_the_role_back()
    {
        var accounts = new FakeVendorAccounts { FailOrganization = true };
        var userId = NewUserId();
        var input = VendorRegistrationInputTests.Valid();

        var result = await RegisterAsync(accounts, userId, input);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.RegistrationFailed);
        accounts.Revoked.ShouldHaveSingleItem().ShouldBe(new VendorAccessGrant(userId, "acme", RoleAdded: true, OrganizationAdded: false));
        (await VendorRows.CompaniesWithCrAsync(db.OwnerConnectionString, input.CrNumber!, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task An_account_with_the_role_and_the_host_organization_but_no_company_completes_its_registration()
    {
        // A registration that died between Keycloak and the database, or lost a race: it may continue.
        var accounts = new FakeVendorAccounts { State = new VendorAccountState(HoldsVendorRole: true, OrganizationAliases: ["acme"]) };
        var userId = NewUserId();

        var result = await RegisterAsync(accounts, userId, VendorRegistrationInputTests.Valid());

        var companyId = result.IsSuccess ? result.Value : throw new ShouldAssertException(result.Error.Message);
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldNotBeNull().CompanyId.ShouldBe(companyId);
        accounts.Revoked.ShouldBeEmpty();
        var audit = (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, userId, "vendor.registered", Ct)).ShouldHaveSingleItem();
        using var data = JsonDocument.Parse(audit.Data);
        data.RootElement.GetProperty("organization_added").GetString().ShouldBe("false");
    }

    [Fact]
    public async Task A_failed_attempt_of_a_resumed_registration_takes_back_nothing_it_did_not_add()
    {
        var accounts = new FakeVendorAccounts { State = new VendorAccountState(HoldsVendorRole: true, OrganizationAliases: ["acme"]) };
        var userId = NewUserId();

        var result = await RegisterAsync(accounts, userId, VendorRegistrationInputTests.Valid(), failRegistration: true);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.RegistrationFailed);
        accounts.Revoked.ShouldAllBe(g => !g.RoleAdded && !g.OrganizationAdded);
    }

    [Theory]
    [InlineData(true, "beta")]
    [InlineData(false, "acme")]
    [InlineData(true, "acme,beta")]
    public async Task An_account_in_another_organization_or_in_the_host_organization_without_the_vendor_role_is_refused_as_staff(
        bool holdsVendorRole, string organizations)
    {
        var accounts = new FakeVendorAccounts { State = new VendorAccountState(holdsVendorRole, organizations.Split(',')) };
        var userId = NewUserId();

        var result = await RegisterAsync(accounts, userId, VendorRegistrationInputTests.Valid());

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.StaffAccount);
        accounts.Steps.ShouldBe(["describe"]);
    }

    [Fact]
    public async Task When_a_parallel_registration_of_the_same_user_wins_nothing_is_taken_back()
    {
        var accounts = new FakeVendorAccounts();
        var userId = NewUserId();
        Guid? winner = null;
        accounts.OnGrantRole = async user =>
            winner = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, user, VendorRows.NewCrNumber(), "Parallel Winner", Ct);

        var result = await RegisterAsync(accounts, userId, VendorRegistrationInputTests.Valid());

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.AlreadyRegistered);
        accounts.Revoked.ShouldBeEmpty();
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldNotBeNull().CompanyId.ShouldBe(winner.ShouldNotBeNull());
    }

    [Fact]
    public async Task A_database_failure_is_not_undone_when_the_user_has_a_company_by_then()
    {
        var accounts = new FakeVendorAccounts();
        var userId = NewUserId();
        accounts.OnGrantRole = user =>
            VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, user, VendorRows.NewCrNumber(), "Parallel Winner", Ct);

        var result = await RegisterAsync(accounts, userId, VendorRegistrationInputTests.Valid(), failRegistration: true);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.AlreadyRegistered);
        accounts.Revoked.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("en-US")]
    public async Task The_culture_the_privacy_notice_was_shown_in_is_stored_with_the_acceptance(string culture)
    {
        var userId = NewUserId();

        var result = await RegisterAsync(new FakeVendorAccounts(), userId, VendorRegistrationInputTests.Valid() with { PrivacyNoticeCulture = culture });

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldNotBeNull().PrivacyNoticeCulture.ShouldBe(culture);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ar")]
    [InlineData("")]
    public async Task A_privacy_notice_culture_other_than_arabic_or_english_is_refused(string culture)
    {
        var accounts = new FakeVendorAccounts();

        var result = await RegisterAsync(accounts, NewUserId(), VendorRegistrationInputTests.Valid() with { PrivacyNoticeCulture = culture });

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.PrivacyNoticeRequired);
        accounts.Steps.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_precheck_answers_as_the_registration_would()
    {
        var staff = NewUserId();
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, staff, $"{staff}@acme.test", [TenantRoles.ContractsOfficer], "active", Ct);
        var vendor = NewUserId();
        await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Beta, vendor, VendorRows.NewCrNumber(), "Already Vendor", Ct);

        (await CheckAsync(new FakeVendorAccounts(), NewUserId())).ShouldBe(VendorRegistrationCheck.Open);
        (await CheckAsync(new FakeVendorAccounts(), staff)).ShouldBe(VendorRegistrationCheck.StaffAccount);
        (await CheckAsync(new FakeVendorAccounts(), vendor)).ShouldBe(VendorRegistrationCheck.AlreadyRegistered);
        (await CheckAsync(new FakeVendorAccounts { State = new VendorAccountState(false, ["beta"]) }, NewUserId()))
            .ShouldBe(VendorRegistrationCheck.StaffAccount);
        (await CheckAsync(new FakeVendorAccounts { State = new VendorAccountState(true, ["acme"]) }, NewUserId()))
            .ShouldBe(VendorRegistrationCheck.Open);
    }

    private async Task<VendorRegistrationCheck> CheckAsync(FakeVendorAccounts accounts, string userId)
    {
        await using var host = Host(accounts, failRegistration: false);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: userId);
        return await scope.ServiceProvider.GetRequiredService<IVendorRegistration>().CheckAsync(Ct);
    }

    private async Task<Result<Guid>> RegisterAsync(FakeVendorAccounts accounts, string userId, VendorRegistration input, bool failRegistration = false)
    {
        await using var host = Host(accounts, failRegistration);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: userId);
        return await scope.ServiceProvider.GetRequiredService<IVendorRegistration>().RegisterCompanyAsync(input, "applicant@example.test", Ct);
    }

    private ModuleHost Host(FakeVendorAccounts accounts, bool failRegistration) => new(db.AppConnectionString, configure: services =>
    {
        services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
        if (failRegistration)
        {
            services.ConfigureDbContext<VendorsDbContext>(o => o.AddInterceptors(new FailingRegistration()));
        }
    });

    private static string NewUserId() => Guid.NewGuid().ToString();

    /// <summary>The database failing the company insert only: every other statement (the checks, the re-check) runs.</summary>
    private sealed class FailingRegistration : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("vendor.register_company", StringComparison.Ordinal)
                ? throw new NpgsqlException("Simulated failure of the vendor store.")
                : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
