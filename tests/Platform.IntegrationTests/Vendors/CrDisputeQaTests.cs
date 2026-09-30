using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// QA pass on PR #5 (W-33, ADR-0013 point 5, vendors spec V-17): the retry's fail-closed paths between two retries. While
/// the tenant lookup fails, the dispute stays listed with its failed steps and the claimant still hears that access is
/// pending; while a removed user's standing (their staff tenants and current vendor company) cannot be read, nothing is
/// taken from them and their steps wait for a retry. <c>R1_...</c> in <c>CrDisputeRetryAttackTests</c> covers the end state
/// after the lookup heals; these cover what holds in between. Also two acceptance points without a test before this pass:
/// an uphold is refused once the claimant became active staff of a tenant, and an undo follows the same staff rule as a
/// removal.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CrDisputeQaTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Acme => TestTenants.Acme.KeycloakOrgAlias;

    private static string Beta => TestTenants.Beta.KeycloakOrgAlias;

    [Fact]
    public async Task A_retry_whose_tenant_lookup_fails_keeps_the_dispute_listed_with_its_failed_steps()
    {
        var (disputeId, _, _, squatter, admin, accounts, faults, host) = await BetaAddFailedAsync("Madinah Printing Press");
        await using var owned = host;

        accounts.FailingOrganizations.Clear();
        faults.CatalogFails = true;
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.StillFailing);

        var failure = (await FailuresAsync(host, admin)).Where(f => f.DisputeId == disputeId).ShouldHaveSingleItem();
        failure.FailedSteps.ShouldContain($"organization:add:{Beta}");
        failure.FailedSteps.ShouldContain($"organization:remove:{Beta}:{squatter}");
        var details = (string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @id", disputeId))!;
        details.ShouldNotContain("skipped: no longer", Case.Sensitive, "a failed lookup never turns a failed step into a final skip");
        ((string)(await OwnerScalarAsync("select idp_outcome from vendor.cr_disputes where id = @id", disputeId))!).ShouldBe("failed");
    }

    [Fact]
    public async Task A_claimant_joining_after_a_retry_whose_tenant_lookup_failed_still_hears_that_access_is_pending()
    {
        var (disputeId, companyId, claimant, _, admin, accounts, faults, host) = await BetaAddFailedAsync("Jazan Fisheries");
        await using var owned = host;
        accounts.FailingOrganizations.Clear();
        faults.CatalogFails = true;
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.StillFailing);
        faults.CatalogFails = false;
        accounts.State = new VendorAccountState(HoldsVendorRole: true, OrganizationAliases: [Acme]);

        await using var scope = host.ScopeFor(TestTenants.Beta, vendorCompanyId: companyId, actingUserId: claimant);
        (await scope.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct)).Error.ShouldNotBeNull()
            .Code.ShouldBe(VendorErrors.MembershipPendingRetry);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, claimant, "vendor.membership_restore_refused", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_uphold_that_cannot_read_a_removed_users_standing_takes_nothing_from_them_and_leaves_the_steps_for_a_retry()
    {
        var (companyId, squatter, crNumber) = await VendorAsync("Asir Building Materials");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        accounts.Memberships[(squatter, Acme)] = true;
        accounts.Memberships[(squatter, Beta)] = true;
        var faults = new Faults { StaffLookupFails = true };
        await using var host = Host(accounts, faults);
        var disputeId = await RaiseAsync(host, claimant, crNumber);

        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse("the squatter's steps could not be decided");

        accounts.OrganizationsOf(squatter).ShouldBe([Acme, Beta], "nothing is taken while the standing is unknown");
        accounts.Revoked.ShouldNotContain(g => g.UserId == squatter);
        accounts.OrganizationsOf(claimant).ShouldBe([Acme, Beta], "the claimant's grants do not wait for the squatter's standing");
        (await FailuresAsync(host, admin)).Where(f => f.DisputeId == disputeId).ShouldHaveSingleItem().FailedSteps
            .ShouldBe([$"organization:remove:{Acme}:{squatter}", $"organization:remove:{Beta}:{squatter}", $"role:revoke:{squatter}"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_retry_once_the_standing_can_be_read_takes_the_removed_users_access_and_leaves_the_list()
    {
        var (companyId, squatter, crNumber) = await VendorAsync("Tabuk Agricultural Supplies");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        accounts.Memberships[(squatter, Acme)] = true;
        var faults = new Faults { StaffLookupFails = true };
        await using var host = Host(accounts, faults);
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();
        var grantsBefore = accounts.Granted.Count;

        faults.StaffLookupFails = false;
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.Updated);

        accounts.OrganizationsOf(squatter).ShouldBeEmpty();
        accounts.Revoked.ShouldContain(g => g.UserId == squatter && g.RoleAdded);
        accounts.Granted.Count.ShouldBe(grantsBefore, "the claimant's done grant is not run again");
        (await FailuresAsync(host, admin)).ShouldNotContain(f => f.DisputeId == disputeId);
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(claimant, "vendor-admin")]);
    }

    [Fact]
    public async Task Upholding_is_refused_when_the_claimant_meanwhile_became_active_staff_of_a_tenant()
    {
        var (companyId, squatter, crNumber) = await VendorAsync("Al Khobar Marine Services");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        await using var host = Host(accounts, new Faults());
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Beta.TenantId, claimant, $"{claimant}@beta.test", [TenantRoles.ContractsOfficer], "active", Ct);

        await using var scope = host.PlatformScope(admin);
        var refused = await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(disputeId, "Checked the CR certificate.", admin, Ct);

        refused.Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.StaffAccount);
        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().Status.ShouldBe("open");
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(squatter, "vendor-admin")]);
        accounts.Steps.ShouldNotContain("grant-role");
    }

    [Fact]
    public async Task An_undo_leaves_the_organization_of_a_tenant_whose_staff_the_claimant_became_while_the_run_went()
    {
        // The run's recheck finds a later uphold moved the company while its steps ran (as in CrDisputeRetryTests'
        // undo tests); by then beta has made the first claimant an active staff member. The undo takes acme's
        // organization, which came from the disputed company alone, and leaves beta's, which is beta's decision now.
        var (companyId, _, crNumber) = await VendorAsync("Yanbu Industrial Gases");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        await using var host = Host(accounts, new Faults());
        var firstDispute = await RaiseAsync(host, first, crNumber);
        var moved = false;
        accounts.OnGrantRole = async u =>
        {
            if (u == first && !moved)
            {
                moved = true;
                await UpholdAsync(host, await RaiseAsync(host, second, crNumber), admin);
                await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Beta.TenantId, first, $"{first}@beta.test", [TenantRoles.TechnicalEvaluator], "active", Ct);
            }
        };

        await UpholdAsync(host, firstDispute, admin);

        moved.ShouldBeTrue();
        accounts.OrganizationsOf(first).ShouldBe([Beta], "the undo took acme and left beta, whose staff the claimant is now");
        var details = (string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @id", firstDispute))!;
        details.ShouldContain($"\"undo:organization:add:{Acme}\": \"done\"");
        details.ShouldContain($"\"undo:organization:add:{Beta}\": \"skipped: the user is the tenant's staff\"");
    }

    // Helpers --------------------------------------------------------------------------------------------------------

    /// <summary>An upheld dispute whose add of the claimant to beta and removal of the squatter from beta failed.</summary>
    private async Task<(Guid DisputeId, Guid CompanyId, string Claimant, string Squatter, string Admin, FakeVendorAccounts Accounts, Faults Faults, ModuleHost Host)>
        BetaAddFailedAsync(string nameEn)
    {
        var (companyId, squatter, crNumber) = await VendorAsync(nameEn);
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        accounts.Memberships[(squatter, Acme)] = true;
        accounts.Memberships[(squatter, Beta)] = true;
        accounts.FailingOrganizations[Beta] = true;
        var faults = new Faults();
        var host = Host(accounts, faults);
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();
        return (disputeId, companyId, claimant, squatter, admin, accounts, faults, host);
    }

    private ModuleHost Host(FakeVendorAccounts accounts, Faults faults) =>
        new(db.AppConnectionString, configure: services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            var catalog = services.Last(d => d.ServiceType == typeof(ITenantCatalog));
            services.Replace(ServiceDescriptor.Scoped<ITenantCatalog>(sp =>
                new FaultyCatalog((ITenantCatalog)ActivatorUtilities.CreateInstance(sp, catalog.ImplementationType!), faults)));
            var staff = services.Last(d => d.ServiceType == typeof(IStaffTenancies));
            services.Replace(ServiceDescriptor.Scoped<IStaffTenancies>(sp =>
                new FaultyStaffTenancies((IStaffTenancies)ActivatorUtilities.CreateInstance(sp, staff.ImplementationType!), faults)));
        });

    private async Task<(Guid CompanyId, string Squatter, string CrNumber)> VendorAsync(string nameEn)
    {
        var squatter = Guid.NewGuid().ToString();
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, squatter, crNumber, nameEn, Ct);
        return (companyId, squatter, crNumber);
    }

    private static async Task<Guid> RaiseAsync(ModuleHost host, string claimant, string crNumber)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
        var raised = await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
            new CrDisputeRequest(crNumber, "The CR certificate names our managing director; someone else registered our company.",
                VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English),
            $"{claimant}@claimant.test", "Majed Al-Dosari", Ct);
        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        return raised.Value;
    }

    private static async Task<CrDisputeUpheld> UpholdAsync(ModuleHost host, Guid disputeId, string admin)
    {
        await using var scope = host.PlatformScope(admin);
        var upheld = await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(disputeId, "Checked the CR certificate.", admin, Ct);
        upheld.IsSuccess.ShouldBeTrue(upheld.Error?.Message);
        return upheld.Value;
    }

    private static async Task<CrDisputeRetry> RetryAsync(ModuleHost host, Guid disputeId, string admin)
    {
        await using var scope = host.PlatformScope(admin);
        return await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().RetryIdentityProviderAsync(disputeId, admin, Ct);
    }

    private static async Task<IReadOnlyList<CrDisputeIdentityProviderFailure>> FailuresAsync(ModuleHost host, string admin)
    {
        await using var scope = host.PlatformScope(admin);
        return await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().ListIdentityProviderFailuresAsync(Ct);
    }

    private async Task<object?> OwnerScalarAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // Fixed statements of this class.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(Ct);
    }

    /// <summary>Switches for the lookups a retry depends on, failing as a database hiccup would while set.</summary>
    private sealed class Faults
    {
        public volatile bool CatalogFails;

        public volatile bool StaffLookupFails;
    }

    private sealed class FaultyCatalog(ITenantCatalog inner, Faults faults) : ITenantCatalog
    {
        public Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            faults.CatalogFails ? throw new InvalidOperationException("The tenant catalog could not be read.") : inner.ListAsync(cancellationToken);
    }

    private sealed class FaultyStaffTenancies(IStaffTenancies inner, Faults faults) : IStaffTenancies
    {
        public Task<IReadOnlySet<Guid>> TenantsOfAsync(string userId, CancellationToken cancellationToken = default) =>
            faults.StaffLookupFails ? throw new TimeoutException("The identity database did not answer.") : inner.TenantsOfAsync(userId, cancellationToken);
    }
}
