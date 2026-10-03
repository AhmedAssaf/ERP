using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// W-33, the platform's side: the check method is a platform setting changed only in the console and audited; the real
/// company raises a dispute on a tenant host; a platform admin upholds it, which moves the company to the claimant as its
/// vendor admin with its ledger, documents and relationships untouched, or rejects it. Only a platform console session
/// reads or resolves disputes (ADR-0012 point 4).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CrDisputeTests(DatabaseFixture db)
{
    private const string Statement = "We are the owners named on the CR certificate; someone else registered our company.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_a_platform_console_session_changes_the_method_and_it_is_audited()
    {
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.TenantAdmin);
        await using var host = Host(new FakeVendorAccounts());
        try
        {
            await using (var scope = host.PlatformScope(admin))
            {
                var administration = scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>();
                (await administration.GetSettingsAsync(Ct)).Method.ShouldBe(CrOwnershipMethod.Manual);
                (await administration.GetSettingsAsync(Ct)).WathqConfigured.ShouldBeFalse();

                var changed = await administration.SetMethodAsync(CrOwnershipMethod.Wathq, admin, Ct);

                changed.IsSuccess.ShouldBeTrue();
                changed.Value.ShouldBe(CrOwnershipMethod.Manual);
                var settings = await administration.GetSettingsAsync(Ct);
                settings.Method.ShouldBe(CrOwnershipMethod.Wathq);
                settings.ChangedBy.ShouldBe(admin);
            }

            var audit = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.ownership_method_changed", Ct)).ShouldHaveSingleItem();
            audit.Data.ShouldContain("\"from\": \"manual\"");
            audit.Data.ShouldContain("\"to\": \"wathq\"");

            // A tenant host's scope may not use the console's service, and its database session may not change the method.
            await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
            {
                await Should.ThrowAsync<InvalidOperationException>(
                    () => scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().SetMethodAsync(CrOwnershipMethod.Manual, officer, Ct));
            }

            var (companyId, vendorUser) = await VendorAsync("Method Probe Co");
            foreach (var (tenant, vendor, user) in new (Guid?, Guid?, string?)[]
                     {
                         (TestTenants.Acme.TenantId, null, officer),
                         (TestTenants.Acme.TenantId, companyId, vendorUser),
                         (null, null, null),
                     })
            {
                await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, vendor, user, Ct);
                await using var command = new NpgsqlCommand("select vendor.set_ownership_method('manual')", session);
                (await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            }

            (await OwnershipRows.MethodAsync(db.OwnerConnectionString, Ct)).ShouldBe("wathq");
        }
        finally
        {
            await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "manual", Ct);
        }
    }

    [Fact]
    public async Task A_claimant_raises_a_dispute_and_a_platform_admin_upholds_it_moving_the_company()
    {
        var (companyId, squatter) = await VendorAsync("Squatted Company");
        var crNumber = await CrNumberAsync(companyId);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, await DatabaseClock.ValidUntilAsync(db.OwnerConnectionString, Ct), "clean", isCurrent: true, Ct);
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var recipient = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, "Dispute Test Recipient", Ct);
        var grant = await ConsentRows.InsertGrantAsOwnerAsync(db.OwnerConnectionString, companyId, recipient, "award_records", 0, 30, squatter, Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        accounts.Memberships[(squatter, TestTenants.Acme.KeycloakOrgAlias)] = true;
        accounts.Memberships[(squatter, TestTenants.Beta.KeycloakOrgAlias)] = true;
        await using var host = Host(accounts);

        Guid disputeId;
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant))
        {
            var disputes = scope.ServiceProvider.GetRequiredService<ICrDisputes>();
            var raised = await disputes.RaiseAsync(Request(crNumber), "real.owner@example.test", "Real Owner", Ct);
            raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
            disputeId = raised.Value;

            // One open dispute per claimant and company.
            (await disputes.RaiseAsync(Request(crNumber), "real.owner@example.test", "Real Owner", Ct)).Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.AlreadyOpen);
            (await disputes.ListOwnAsync(Ct)).ShouldHaveSingleItem().ShouldBe(new OwnCrDispute(disputeId, crNumber, (await disputes.ListOwnAsync(Ct))[0].RaisedAt, "open"));
        }

        var row = (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull();
        row.ShouldBe(new CrDisputeRow(companyId, claimant, "real.owner@example.test", "Real Owner", "open", TestTenants.Acme.TenantId, null, null, null));
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, claimant, "vendor.dispute_raised", Ct)).ShouldHaveSingleItem().SubjectId.ShouldBe(disputeId.ToString());

        // While the dispute is open, no officer verifies the company.
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();
            (await directory.GetOwnershipCheckAsync(companyId, Ct)).ShouldNotBeNull().Disputed.ShouldBeTrue();
            var held = await directory.ApproveAsync(companyId, officer, new OwnershipConfirmation("Certificate names the registrant.", false, CrLookupOutcome.Manual), Ct);
            held.Error.ShouldNotBeNull().Code.ShouldBe(CrOwnershipErrors.Disputed);
        }

        await using (var scope = host.PlatformScope(admin))
        {
            var administration = scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>();
            var open = (await administration.ListOpenDisputesAsync(Ct)).Single(d => d.Id == disputeId);
            open.CrNumber.ShouldBe(crNumber);
            open.CompanyNameEn.ShouldBe("Squatted Company");
            open.ClaimantEmail.ShouldBe("real.owner@example.test");
            open.RegistrantUserId.ShouldBe(squatter);
            open.RaisedOnTenant.ShouldBe(TestTenants.Acme.TenantId);

            (await administration.UpholdAsync(disputeId, "  ", admin, Ct)).Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.NoteRequired);
            var upheld = await administration.UpholdAsync(disputeId, "Checked the CR certificate and the claimant's authorisation.", admin, Ct);

            upheld.IsSuccess.ShouldBeTrue(upheld.Error?.Message);
            upheld.Value.ShouldBe(new CrDisputeUpheld(companyId, claimant, upheld.Value.RemovedUserIds, IdentityProviderUpdated: true));
            upheld.Value.RemovedUserIds.ShouldBe([squatter]);
            (await administration.ListOpenDisputesAsync(Ct)).ShouldNotContain(d => d.Id == disputeId);
            (await administration.UpholdAsync(disputeId, "Again.", admin, Ct)).Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.NotOpen);
        }

        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(claimant, "vendor-admin")]);
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, squatter, Ct)).ShouldBeNull();
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, claimant, Ct)).ShouldNotBeNull().PrivacyNoticeVersion.ShouldBe(VendorPrivacyNotice.CurrentVersion);
        var verification = (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldNotBeNull();
        verification.ShouldBe(new OwnershipVerificationRow("dispute", claimant, admin, null, "Checked the CR certificate and the claimant's authorisation."));
        var closed = (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull();
        closed.Status.ShouldBe("upheld");
        closed.ResolvedBy.ShouldBe(admin);
        closed.RemovedUserIds.ShouldBe([squatter]);
        // The company keeps its ledger, documents and relationships (ADR-0010).
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().Id.ShouldBe(grant);
        (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).Keys.ShouldBe(
            new[] { TestTenants.Acme.TenantId, TestTenants.Beta.TenantId }.Order());
        // The identity provider: the claimant gets the vendor role and both related tenants' organizations; the squatter
        // loses the role and both memberships.
        accounts.Granted.ShouldBe([claimant]);
        accounts.OrganizationsOf(claimant).ShouldBe([TestTenants.Acme.KeycloakOrgAlias, TestTenants.Beta.KeycloakOrgAlias]);
        accounts.OrganizationsOf(squatter).ShouldBeEmpty();
        accounts.Revoked.ShouldContain(new VendorAccessGrant(squatter, string.Empty, RoleAdded: true, OrganizationAdded: false));
        var audit = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_upheld", Ct)).ShouldHaveSingleItem();
        audit.SubjectId.ShouldBe(disputeId.ToString());
        audit.Data.ShouldContain(squatter);

        // The new vendor admin sees the grant the squatter made in the company's ledger.
        await using (var scope = host.ScopeFor(TestTenants.Acme, vendorCompanyId: companyId, actingUserId: claimant))
        {
            (await scope.ServiceProvider.GetRequiredService<IConsentLedger>().ListAsync(Ct)).ShouldHaveSingleItem().Id.ShouldBe(grant);
        }

        // Ownership is now verified, so an officer approves without a check.
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, officer, Ct)).IsSuccess.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_rejected_dispute_changes_nothing_else()
    {
        var (companyId, registrant) = await VendorAsync("Rightful Registrant Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, await CrNumberAsync(companyId));

        await using (var scope = host.PlatformScope(admin))
        {
            var rejected = await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>()
                .RejectAsync(disputeId, "The claimant is not named on the certificate.", admin, Ct);
            rejected.IsSuccess.ShouldBeTrue(rejected.Error?.Message);
        }

        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().Status.ShouldBe("rejected");
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(registrant, "vendor-admin")]);
        (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeNull();
        accounts.Granted.ShouldBeEmpty();
        accounts.Revoked.ShouldBeEmpty();
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_rejected", Ct)).ShouldHaveSingleItem().SubjectId.ShouldBe(disputeId.ToString());
    }

    [Fact]
    public async Task A_dispute_needs_a_company_under_the_cr_number_and_a_person_who_is_neither_vendor_nor_staff()
    {
        var (companyId, vendorUser) = await VendorAsync("Dispute Guard Co");
        var crNumber = await CrNumberAsync(companyId);
        var staff = await StaffAsync(TestTenants.Acme, TenantRoles.TechnicalEvaluator);
        var stranger = Guid.NewGuid().ToString();
        await using var host = Host(new FakeVendorAccounts());

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: stranger))
        {
            var disputes = scope.ServiceProvider.GetRequiredService<ICrDisputes>();
            (await disputes.RaiseAsync(Request(VendorRows.NewCrNumber()), "x@example.test", "X", Ct)).Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.NoCompany);
            (await disputes.RaiseAsync(Request("12345"), "x@example.test", "X", Ct)).Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.InvalidCrNumber);
            (await disputes.RaiseAsync(new CrDisputeRequest(crNumber, " ", VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English), "x@example.test", "X", Ct))
                .Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.StatementRequired);
            (await disputes.RaiseAsync(new CrDisputeRequest(crNumber, Statement, null, VendorPrivacyNotice.English), "x@example.test", "X", Ct))
                .Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.PrivacyNoticeRequired);
        }

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: vendorUser))
        {
            (await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), "v@example.test", "V", Ct))
                .Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.AlreadyVendor);
        }

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: staff))
        {
            (await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), "s@example.test", "S", Ct))
                .Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.StaffAccount);
        }

        // The number without a company counted toward the duplicate-CR limit: four more misses and the person is limited.
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: stranger))
        {
            var disputes = scope.ServiceProvider.GetRequiredService<ICrDisputes>();
            for (var i = 0; i < 4; i++)
            {
                (await disputes.RaiseAsync(Request(VendorRows.NewCrNumber()), "x@example.test", "X", Ct)).Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.NoCompany);
            }

            (await disputes.RaiseAsync(Request(crNumber), "x@example.test", "X", Ct)).Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.Limited);
        }
    }

    [Fact]
    public async Task Upholding_is_refused_when_the_claimant_meanwhile_belongs_to_a_company()
    {
        var (companyId, registrant) = await VendorAsync("Contested Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts());
        var disputeId = await RaiseAsync(host, claimant, await CrNumberAsync(companyId));
        await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Beta, claimant, VendorRows.NewCrNumber(), "Claimant's Own Co", Ct);

        await using (var scope = host.PlatformScope(admin))
        {
            var refused = await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(disputeId, "Checked.", admin, Ct);
            refused.Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.AlreadyVendor);
        }

        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().Status.ShouldBe("open");
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(registrant, "vendor-admin")]);
    }

    [Fact]
    public async Task Disputes_are_read_and_resolved_only_by_a_platform_console_session()
    {
        var (companyId, vendorUser) = await VendorAsync("Dispute Isolation Co");
        var claimant = Guid.NewGuid().ToString();
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.TenantAdmin);
        await using var host = Host(new FakeVendorAccounts());
        var disputeId = await RaiseAsync(host, claimant, await CrNumberAsync(companyId));

        foreach (var (tenant, vendor, user) in new (Guid?, Guid?, string?)[]
                 {
                     (TestTenants.Acme.TenantId, null, officer),
                     (TestTenants.Acme.TenantId, companyId, vendorUser),
                     (TestTenants.Acme.TenantId, null, claimant),
                 })
        {
            await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, vendor, user, Ct);
            await using (var select = new NpgsqlCommand("select count(*)::int from vendor.cr_disputes", session))
            {
                ((int)(await select.ExecuteScalarAsync(Ct))!).ShouldBe(0);
            }

            await using (var open = new NpgsqlCommand("select count(*)::int from vendor.open_cr_disputes()", session))
            {
                ((int)(await open.ExecuteScalarAsync(Ct))!).ShouldBe(0);
            }

            await using var resolve = new NpgsqlCommand("select * from vendor.resolve_cr_dispute(@id, true, 'Forged.')", session);
            resolve.Parameters.AddWithValue("id", disputeId);
            (await Should.ThrowAsync<PostgresException>(() => resolve.ExecuteScalarAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // The claimant lists their own through the function; nobody else sees it there.
        await using (var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Beta.TenantId, null, claimant, Ct))
        await using (var own = new NpgsqlCommand("select count(*)::int from vendor.my_cr_disputes() where id = @id", session))
        {
            own.Parameters.AddWithValue("id", disputeId);
            ((int)(await own.ExecuteScalarAsync(Ct))!).ShouldBe(1);
        }

        await using (var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Acme.TenantId, null, officer, Ct))
        await using (var own = new NpgsqlCommand("select count(*)::int from vendor.my_cr_disputes()", session))
        {
            ((int)(await own.ExecuteScalarAsync(Ct))!).ShouldBe(0);
        }

        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().Status.ShouldBe("open");

        // The console's service refuses outside a platform request as well.
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            await Should.ThrowAsync<InvalidOperationException>(
                () => scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().ListOpenDisputesAsync(Ct));
        }
    }

    private static CrDisputeRequest Request(string crNumber) =>
        new(crNumber, Statement, VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English);

    private ModuleHost Host(FakeVendorAccounts accounts) =>
        new(db.AppConnectionString, configure: services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

    private static async Task<Guid> RaiseAsync(ModuleHost host, string claimant, string crNumber)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
        var raised = await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), $"{claimant}@example.test", "Claimant", Ct);
        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        return raised.Value;
    }

    private async Task<(Guid CompanyId, string UserId)> VendorAsync(string nameEn)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        return (companyId, userId);
    }

    private async Task<string> CrNumberAsync(Guid companyId)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select cr_number from vendor.companies where id = @id", connection);
        command.Parameters.AddWithValue("id", companyId);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<string> StaffAsync(Platform.Shared.Tenancy.TenantContext tenant, string role)
    {
        var userId = $"{role}-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, userId, $"{userId}@{tenant.Slug}.test", [role], "active", Ct);
        return userId;
    }
}
