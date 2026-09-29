using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Ownership;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// W-33 after review and pentest (ADR-0013 decision 1 and the reviewer's items): a dispute holds a verified company's
/// approvals only once a platform admin accepted it for review, and then the database itself refuses the approval; a
/// company has at most five pending disputes; competing upholds leave one vendor admin and the earlier verification stays
/// on the dispute; an identity provider failure after an uphold is stored, listed and retried; an unchanged method is not
/// audited; no paid Wathq call is made when the check cannot pass; and the platform admins hear of new disputes once.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CrDisputeTriageTests(DatabaseFixture db)
{
    private const string Statement = "We are the owners named on the CR certificate; someone else registered our company.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_dispute_accepted_for_review_holds_a_verified_companys_approval_in_the_database()
    {
        var (companyId, _, crNumber) = await VendorAsync("Accepted Hold Co");
        await OwnershipRows.VerifyAsOwnerAsync(db.OwnerConnectionString, companyId, TestTenants.Acme.TenantId, Ct);
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var betaOfficer = await StaffAsync(TestTenants.Beta, TenantRoles.ContractsOfficer);
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts());
        var disputeId = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber);

        await using (var scope = host.PlatformScope(admin))
        {
            var accepted = await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().AcceptForReviewAsync(disputeId, admin, Ct);
            accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
            (await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().AcceptForReviewAsync(disputeId, admin, Ct))
                .Error.ShouldNotBeNull().Code.ShouldBe(CrDisputeErrors.NotOpen);
            (await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().ListOpenDisputesAsync(Ct))
                .Single(d => d.Id == disputeId).Status.ShouldBe(CrDisputeStatus.UnderReview);
        }

        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_accepted", Ct)).ShouldHaveSingleItem().SubjectId.ShouldBe(disputeId.ToString());

        // The service has no check of its own for a verified company: the refusal is the database's (ck_relationships_not_disputed).
        await using (var scope = host.ScopeFor(TestTenants.Beta, actingUserId: betaOfficer))
        {
            var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();
            (await directory.GetOwnershipCheckAsync(companyId, Ct)).ShouldNotBeNull().Disputed.ShouldBeTrue();
            (await directory.ApproveAsync(companyId, betaOfficer, Ct)).Error.ShouldNotBeNull().Code.ShouldBe(CrOwnershipErrors.Disputed);
        }

        await using (var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Beta.TenantId, null, betaOfficer, Ct))
        await using (var command = new NpgsqlCommand("select vendor.approve_relationship(@company)", session))
        {
            command.Parameters.AddWithValue("company", companyId);
            var refused = await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct));
            (refused.SqlState, refused.ConstraintName).ShouldBe((PostgresErrorCodes.CheckViolation, "ck_relationships_not_disputed"));
        }

        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Beta.TenantId].ShouldBe("pending");
    }

    [Fact]
    public async Task A_new_dispute_holds_nothing_for_a_verified_company_but_holds_the_first_verification_of_an_unverified_one()
    {
        var (verified, _, verifiedCr) = await VendorAsync("Verified Not Held Co");
        await OwnershipRows.VerifyAsOwnerAsync(db.OwnerConnectionString, verified, TestTenants.Acme.TenantId, Ct);
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, verified, Ct);
        var (unverified, _, unverifiedCr) = await VendorAsync("Unverified Held Co");
        var acmeOfficer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        var betaOfficer = await StaffAsync(TestTenants.Beta, TenantRoles.ContractsOfficer);
        await using var host = Host(new FakeVendorAccounts());
        await RaiseAsync(host, Guid.NewGuid().ToString(), verifiedCr);
        await RaiseAsync(host, Guid.NewGuid().ToString(), unverifiedCr);

        await using (var scope = host.ScopeFor(TestTenants.Beta, actingUserId: betaOfficer))
        {
            var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();
            (await directory.GetOwnershipCheckAsync(verified, Ct)).ShouldNotBeNull().Disputed.ShouldBeFalse();
            (await directory.ApproveAsync(verified, betaOfficer, Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: acmeOfficer))
        {
            var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();
            (await directory.GetOwnershipCheckAsync(unverified, Ct)).ShouldNotBeNull().Disputed.ShouldBeTrue();
            (await directory.ApproveAsync(unverified, acmeOfficer, new OwnershipConfirmation("Checked the certificate.", false, CrLookupOutcome.Manual), Ct))
                .Error.ShouldNotBeNull().Code.ShouldBe(CrOwnershipErrors.Disputed);
        }
    }

    [Fact]
    public async Task The_real_owners_dispute_after_five_throwaway_ones_is_recorded_flagged_listed_and_can_be_accepted()
    {
        // Second review: a squatter could fill a verified company's pending slots from throwaway accounts (open disputes
        // hold nothing). The company's cap never refuses a dispute; it only flags the ones beyond it.
        var (companyId, _, crNumber) = await VendorAsync("Five Throwaways Co");
        await OwnershipRows.VerifyAsOwnerAsync(db.OwnerConnectionString, companyId, TestTenants.Acme.TenantId, Ct);
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts());
        for (var i = 0; i < 5; i++)
        {
            await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber);
        }

        var realOwner = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber, email: "real.owner@example.test");

        await using var scope = host.PlatformScope(admin);
        var administration = scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>();
        var listed = (await administration.ListOpenDisputesAsync(Ct)).Where(d => d.CompanyId == companyId).ToList();
        listed.Count.ShouldBe(6);
        var flagged = listed.Single(d => d.Id == realOwner);
        (flagged.OverCap, flagged.CompanyPending).ShouldBe((true, 6));
        listed.Where(d => d.Id != realOwner).ShouldAllBe(d => !d.OverCap);
        // Listed after the ones within the cap.
        listed[^1].Id.ShouldBe(realOwner);

        (await administration.AcceptForReviewAsync(realOwner, admin, Ct)).IsSuccess.ShouldBeTrue();
        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, realOwner, Ct)).ShouldNotBeNull().Status.ShouldBe("under_review");
    }

    [Fact]
    public async Task When_the_identity_provider_outcome_cannot_be_recorded_the_admin_is_told_and_the_dispute_stays_listed()
    {
        var (_, _, crNumber) = await VendorAsync("Recorder Down Co");
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = new ModuleHost(db.AppConnectionString, configure: services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => new FakeVendorAccounts()));
            var original = services.Last(d => d.ServiceType == typeof(IPlatformAudit));
            services.Replace(ServiceDescriptor.Scoped<IPlatformAudit>(sp =>
                new FailingOutcomeAudit((IPlatformAudit)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!))));
        });
        var disputeId = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber);

        await using var scope = host.PlatformScope(admin);
        var administration = scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>();
        var upheld = await administration.UpholdAsync(disputeId, "Checked.", admin, Ct);

        upheld.IsSuccess.ShouldBeTrue(upheld.Error?.Message);
        upheld.Value.IdentityProviderUpdated.ShouldBeTrue();
        upheld.Value.IdentityProviderOutcomeRecorded.ShouldBeFalse();
        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().Status.ShouldBe("upheld");
        (await OwnerScalarAsync("select idp_outcome from vendor.cr_disputes where id = @company", disputeId)).ShouldBe(DBNull.Value);
        (await administration.ListIdentityProviderFailuresAsync(Ct)).ShouldContain(f => f.DisputeId == disputeId);
    }

    [Fact]
    public async Task A_related_tenant_missing_from_the_catalog_is_skipped_with_a_reason_not_failed()
    {
        var (companyId, _, crNumber) = await VendorAsync("Gone Tenant Co");
        var goneTenant = Guid.NewGuid();
        await VendorRows.RelateAsync(db.OwnerConnectionString, goneTenant, companyId, Ct);
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts());
        var disputeId = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber);

        await using (var scope = host.PlatformScope(admin))
        {
            var administration = scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>();
            (await administration.UpholdAsync(disputeId, "Checked.", admin, Ct)).Value.IdentityProviderUpdated.ShouldBeTrue();
            (await administration.ListIdentityProviderFailuresAsync(Ct)).ShouldNotContain(f => f.DisputeId == disputeId);
        }

        var details = (string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @company", disputeId))!;
        details.ShouldContain($"\"organization:lookup:{goneTenant:D}\": \"skipped: the tenant is not in the tenant catalog\"");
    }

    [Fact]
    public async Task Multi_line_notes_and_statements_are_stored_with_line_feeds()
    {
        var (companyId, _, crNumber) = await VendorAsync("Multi Line Co");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts());

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            var approved = await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(
                companyId, officer, new OwnershipConfirmation("Checked the letter.\r\nCalled the company.", false, CrLookupOutcome.Manual), Ct);
            approved.IsSuccess.ShouldBeTrue(approved.Error?.Message);
        }

        (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldNotBeNull().Note.ShouldBe("Checked the letter.\nCalled the company.");

        Guid disputeId;
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: Guid.NewGuid().ToString()))
        {
            var raised = await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
                new CrDisputeRequest(crNumber, "First paragraph.\r\n\r\nSecond paragraph.", VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English),
                "multi@example.test", "Multi", Ct);
            raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
            disputeId = raised.Value;
        }

        ((string)(await OwnerScalarAsync("select statement from vendor.cr_disputes where id = @company", disputeId))!).ShouldBe("First paragraph.\n\nSecond paragraph.");

        await using (var scope = host.PlatformScope(admin))
        {
            (await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().RejectAsync(disputeId, "Line one.\rLine two.", admin, Ct)).IsSuccess.ShouldBeTrue();
        }

        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().ResolutionNote.ShouldBe("Line one.\nLine two.");
    }

    [Fact]
    public async Task Competing_upholds_leave_one_vendor_admin_and_close_the_other_dispute()
    {
        var (companyId, _, crNumber) = await VendorAsync("Two Claimants Co");
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await using var host = Host(new FakeVendorAccounts());
        var firstDispute = await RaiseAsync(host, first, crNumber);
        var secondDispute = await RaiseAsync(host, second, crNumber);
        var admins = new[] { $"platform-admin-{Guid.NewGuid():N}", $"platform-admin-{Guid.NewGuid():N}" };

        var results = await Task.WhenAll(new[] { (firstDispute, admins[0]), (secondDispute, admins[1]) }.Select(pair => Task.Run(async () =>
        {
            await using var scope = host.PlatformScope(pair.Item2);
            return await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(pair.Item1, "Checked the certificate.", pair.Item2, Ct);
        }, Ct)));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Single(r => !r.IsSuccess).Error!.Code.ShouldBe(CrDisputeErrors.NotOpen);
        var winner = results.Single(r => r.IsSuccess).Value.ClaimantUserId;
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(winner, "vendor-admin")]);
        var statuses = new[]
        {
            (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, firstDispute, Ct))!.Status,
            (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, secondDispute, Ct))!.Status,
        };
        statuses.Order(StringComparer.Ordinal).ShouldBe(["rejected", "upheld"]);
    }

    [Fact]
    public async Task An_uphold_keeps_the_verification_it_replaces_on_the_dispute()
    {
        var (companyId, squatter, crNumber) = await VendorAsync("Replaced Verification Co");
        await OwnershipRows.VerifyAsOwnerAsync(db.OwnerConnectionString, companyId, TestTenants.Acme.TenantId, Ct);
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts());
        var disputeId = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber);

        await using (var scope = host.PlatformScope(admin))
        {
            (await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(disputeId, "Checked.", admin, Ct)).IsSuccess.ShouldBeTrue();
        }

        var superseded = (string)(await OwnerScalarAsync("select superseded_verification::text from vendor.cr_disputes where id = @company", disputeId))!;
        superseded.ShouldContain("\"method\": \"manual\"");
        superseded.ShouldContain($"\"registrant_user_id\": \"{squatter}\"");
        superseded.ShouldContain("\"verified_by\": \"test-officer\"");
        (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldNotBeNull().Method.ShouldBe("dispute");
    }

    [Fact]
    public async Task An_uphold_whose_identity_provider_update_fails_is_stored_listed_audited_and_retried()
    {
        var (companyId, squatter, crNumber) = await VendorAsync("Keycloak Down Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts
        {
            OnGrantRole = _ => throw new IdentityProviderException("Keycloak did not grant the vendor role.", new HttpRequestException("forced")),
            FailRevoke = true,
        };
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);

        await using (var scope = host.PlatformScope(admin))
        {
            var administration = scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>();
            var upheld = await administration.UpholdAsync(disputeId, "Checked.", admin, Ct);

            upheld.IsSuccess.ShouldBeTrue(upheld.Error?.Message);
            upheld.Value.IdentityProviderUpdated.ShouldBeFalse();
            var failure = (await administration.ListIdentityProviderFailuresAsync(Ct)).Single(f => f.DisputeId == disputeId);
            (failure.CompanyId, failure.ClaimantUserId).ShouldBe((companyId, claimant));
            failure.RemovedUserIds.ShouldBe([squatter]);
        }

        ((string)(await OwnerScalarAsync("select idp_outcome from vendor.cr_disputes where id = @company", disputeId))!).ShouldBe("failed");
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_identity_provider", Ct))
            .ShouldHaveSingleItem().Data.ShouldContain("\"outcome\": \"failed\"");

        // Keycloak answers again: the retry succeeds, is recorded and audited, and the dispute leaves the list.
        accounts.OnGrantRole = null;
        accounts.FailRevoke = false;
        await using (var scope = host.PlatformScope(admin))
        {
            var administration = scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>();
            (await administration.RetryIdentityProviderAsync(disputeId, admin, Ct)).ShouldBeTrue();
            (await administration.ListIdentityProviderFailuresAsync(Ct)).ShouldNotContain(f => f.DisputeId == disputeId);
        }

        ((string)(await OwnerScalarAsync("select idp_outcome from vendor.cr_disputes where id = @company", disputeId))!).ShouldBe("updated");
        accounts.Granted.ShouldContain(claimant);
        accounts.Revoked.ShouldContain(g => g.UserId == squatter && g.RoleAdded && !g.OrganizationAdded);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_identity_provider", Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_uphold_moves_every_related_organization_membership_and_a_partial_failure_is_recorded_and_retried()
    {
        // W-21 closed the membership restore through /vendor/join (P-1), so the uphold itself gives the claimant each
        // related tenant's organization and takes the removed users out of them.
        var (companyId, squatter, crNumber) = await VendorAsync("Organizations Moved Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var acme = TestTenants.Acme.KeycloakOrgAlias;
        var beta = TestTenants.Beta.KeycloakOrgAlias;
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        accounts.Memberships[(squatter, acme)] = true;
        accounts.Memberships[(squatter, beta)] = true;
        accounts.FailingOrganizations[beta] = true;
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);

        await using (var scope = host.PlatformScope(admin))
        {
            var administration = scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>();
            var upheld = await administration.UpholdAsync(disputeId, "Checked.", admin, Ct);

            upheld.IsSuccess.ShouldBeTrue(upheld.Error?.Message);
            upheld.Value.IdentityProviderUpdated.ShouldBeFalse();
            (await administration.ListIdentityProviderFailuresAsync(Ct)).Single(f => f.DisputeId == disputeId).FailedSteps
                .ShouldBe([$"organization:add:{beta}", $"organization:remove:{beta}:{squatter}"]);
        }

        // Acme's organization moved; beta's did not.
        accounts.OrganizationsOf(claimant).ShouldBe([acme]);
        accounts.OrganizationsOf(squatter).ShouldBe([beta]);
        var details = (string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @company", disputeId))!;
        details.ShouldContain($"\"organization:add:{acme}\": \"done\"");
        details.ShouldContain($"\"organization:add:{beta}\": \"failed\"");
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_identity_provider", Ct))
            .ShouldHaveSingleItem().Data.ShouldContain($"\"organization:add:{beta}\": \"failed\"");

        accounts.FailingOrganizations.Clear();
        await using (var scope = host.PlatformScope(admin))
        {
            var administration = scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>();
            (await administration.RetryIdentityProviderAsync(disputeId, admin, Ct)).ShouldBeTrue();
            (await administration.ListIdentityProviderFailuresAsync(Ct)).ShouldNotContain(f => f.DisputeId == disputeId);
        }

        accounts.OrganizationsOf(claimant).ShouldBe([acme, beta]);
        accounts.OrganizationsOf(squatter).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_platform_admin_cannot_accept_their_own_dispute_for_review()
    {
        var (_, _, crNumber) = await VendorAsync("Self Accept Co");
        var claimant = Guid.NewGuid().ToString();
        await using var host = Host(new FakeVendorAccounts());
        var disputeId = await RaiseAsync(host, claimant, crNumber);

        await using (var scope = host.PlatformScope(claimant))
        {
            await Should.ThrowAsync<InvalidOperationException>(
                () => scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().AcceptForReviewAsync(disputeId, claimant, Ct));
        }

        await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, null, null, claimant, Ct);
        await using var command = new NpgsqlCommand("select vendor.accept_cr_dispute(@id)", session);
        command.Parameters.AddWithValue("id", disputeId);
        (await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().Status.ShouldBe("open");
    }

    [Fact]
    public async Task Saving_the_method_it_already_is_writes_no_audit_entry()
    {
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts());
        await using var scope = host.PlatformScope(admin);

        (await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().SetMethodAsync(CrOwnershipMethod.Manual, admin, Ct)).IsSuccess.ShouldBeTrue();

        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.ownership_method_changed", Ct)).ShouldBeEmpty();
        (await OwnershipRows.MethodAsync(db.OwnerConnectionString, Ct)).ShouldBe("manual");
    }

    [Fact]
    public async Task No_paid_wathq_call_is_made_for_a_company_without_a_certificate_or_held_by_a_dispute()
    {
        var calls = new ConcurrentQueue<string>();
        await using var wathq = await FakeHttpServer.StartAsync(async context =>
        {
            calls.Enqueue(context.Request.Path.Value!);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[]", context.RequestAborted);
        }, Ct);
        var (noCertificate, _, _) = await VendorAsync("No Certificate Wathq Co", certificate: false);
        var (held, _, heldCr) = await VendorAsync("Held Wathq Co");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "wathq", Ct);
        try
        {
            await using var host = new ModuleHost(db.AppConnectionString, configure: services =>
            {
                services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => new FakeVendorAccounts()));
                services.Configure<WathqOptions>(o =>
                {
                    o.BaseUrl = wathq.BaseAddress;
                    o.ApiKey = "test-wathq-key";
                });
            });
            await RaiseAsync(host, Guid.NewGuid().ToString(), heldCr);
            await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
            var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();

            (await directory.GetOwnershipCheckAsync(noCertificate, Ct)).ShouldNotBeNull().Lookup.ShouldBeNull();
            (await directory.GetOwnershipCheckAsync(held, Ct)).ShouldNotBeNull().Lookup.ShouldBeNull();
        }
        finally
        {
            await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "manual", Ct);
        }

        calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_platform_admins_hear_of_new_disputes_once_without_personal_data()
    {
        // Earlier tests' disputes are announced by the first run too; this test counts only its own.
        var (_, _, crNumber) = await VendorAsync("Alerted Dispute Co");
        var alerts = new RecordingAlerts();
        await using var host = new ModuleHost(db.AppConnectionString, configure: services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => new FakeVendorAccounts()));
            services.AddSingleton<IPlatformAlerts>(alerts);
            services.AddScoped<CrDisputeAlertJob>();
        });
        var disputeId = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber, email: "private.person@example.test");

        await using (var scope = host.ScopeFor(null))
        {
            await scope.ServiceProvider.GetRequiredService<CrDisputeAlertJob>().RunAsync(Ct);
        }

        var alert = alerts.Sent.ShouldHaveSingleItem();
        $"{alert.Subject} {alert.Body}".ShouldNotContain("private.person@example.test");
        $"{alert.Subject} {alert.Body}".ShouldNotContain(crNumber);
        ((DateTime?)await OwnerScalarAsync("select alerted_at from vendor.cr_disputes where id = @company", disputeId)).ShouldNotBeNull();

        await using (var scope = host.ScopeFor(null))
        {
            await scope.ServiceProvider.GetRequiredService<CrDisputeAlertJob>().RunAsync(Ct);
        }

        alerts.Sent.Count.ShouldBe(1);

        // Only the worker's session may list or mark: a platform console session (an acting user) and a tenant session may not.
        foreach (var (tenant, user) in new (Guid?, string?)[] { (null, "platform-admin"), (TestTenants.Acme.TenantId, "someone") })
        {
            await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, null, user, Ct);
            await using (var list = new NpgsqlCommand("select count(*)::int from vendor.unalerted_cr_disputes(1000)", session))
            {
                ((int)(await list.ExecuteScalarAsync(Ct))!).ShouldBe(0);
            }

            await using var mark = new NpgsqlCommand("select vendor.mark_cr_disputes_alerted(array[@id])", session);
            mark.Parameters.AddWithValue("id", disputeId);
            (await Should.ThrowAsync<PostgresException>(() => mark.ExecuteScalarAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    private static CrDisputeRequest Request(string crNumber) =>
        new(crNumber, Statement, VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English);

    private ModuleHost Host(FakeVendorAccounts accounts) =>
        new(db.AppConnectionString, configure: services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

    private static async Task<Guid> RaiseAsync(ModuleHost host, string claimant, string crNumber, string? email = null)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
        var raised = await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), email ?? $"{claimant}@example.test", "Claimant", Ct);
        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        return raised.Value;
    }

    private async Task<(Guid CompanyId, string UserId, string CrNumber)> VendorAsync(string nameEn, bool certificate = true)
    {
        var userId = Guid.NewGuid().ToString();
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, crNumber, nameEn, Ct);
        if (certificate)
        {
            await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, new DateOnly(2031, 1, 1), "clean", isCurrent: true, Ct);
        }

        return (companyId, userId, crNumber);
    }

    private async Task<string> StaffAsync(Platform.Shared.Tenancy.TenantContext tenant, string role)
    {
        var userId = $"{role}-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, userId, $"{userId}@{tenant.Slug}.test", [role], "active", Ct);
        return userId;
    }

    private async Task<object?> OwnerScalarAsync(string sql, Guid value)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("company", value);
        return await command.ExecuteScalarAsync(Ct);
    }

    /// <summary>The real platform audit, except that the entry recording an identity provider outcome fails.</summary>
    private sealed class FailingOutcomeAudit(IPlatformAudit inner) : IPlatformAudit
    {
        public Task WriteAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default) =>
            entry.Action == "vendor.dispute_identity_provider"
                ? throw new InvalidOperationException("The platform audit is not available.")
                : inner.WriteAsync(entry, cancellationToken);
    }

    private sealed class RecordingAlerts : IPlatformAlerts
    {
        public ConcurrentQueue<PlatformAlert> Sent { get; } = new();

        public Task SendAsync(PlatformAlert alert, CancellationToken cancellationToken = default)
        {
            Sent.Enqueue(alert);
            return Task.CompletedTask;
        }
    }
}
