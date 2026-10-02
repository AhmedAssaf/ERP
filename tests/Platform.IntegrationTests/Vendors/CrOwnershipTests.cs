using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// W-33, the officer's side: a company is approved for the first time only after an officer confirmed its ownership
/// against the CR certificate (Manual) or the CR's owners and managers from Wathq (Wathq); the service and the database
/// both refuse an unverified approval; a verification serves every later tenant without telling it who verified; Wathq
/// failing in any way falls back to the manual check, never to an unverified approval.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CrOwnershipTests(DatabaseFixture db)
{
    private const string Note = "The CR certificate names Registered Person as the owner.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unverified_company_cannot_be_approved_without_an_ownership_check()
    {
        var (companyId, _) = await VendorWithCertificateAsync("Unverified Approval Co");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using var host = Host(new FakeVendorAccounts());

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            var refused = await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, officer, Ct);
            refused.Error.ShouldNotBeNull().Code.ShouldBe(CrOwnershipErrors.Unverified);
        }

        // The database refuses it on its own, whatever the application does.
        await using (var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Acme.TenantId, null, officer, Ct))
        await using (var command = new NpgsqlCommand("select vendor.approve_relationship(@company)", session))
        {
            command.Parameters.AddWithValue("company", companyId);
            var ex = await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct));
            ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            ex.ConstraintName.ShouldBe("ck_relationships_ownership_verified");
        }

        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");
        (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task An_officer_confirms_ownership_against_the_cr_certificate_and_approves_in_one_step()
    {
        var (companyId, registrant) = await VendorWithCertificateAsync("Manual Check Trading");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        var accounts = new FakeVendorAccounts();
        accounts.Profiles[registrant] = new VendorAccountProfile("Registered", "Person", "owner@manual-check.test", EmailVerified: true);
        await using var host = Host(accounts);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
        var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();

        var check = (await directory.GetOwnershipCheckAsync(companyId, Ct)).ShouldNotBeNull();

        check.Verified.ShouldBeFalse();
        check.HasCertificate.ShouldBeTrue();
        check.Disputed.ShouldBeFalse();
        check.Registrant.ShouldBe(new VendorRegistrant(registrant, "Registered Person", "owner@manual-check.test", EmailVerified: true));
        check.Lookup.ShouldNotBeNull().Outcome.ShouldBe(CrLookupOutcome.Manual);

        var result = await directory.ApproveAsync(companyId, officer, new OwnershipConfirmation(Note, BasedOnWathq: false, CrLookupOutcome.Manual), Ct);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("approved");
        var verification = (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldNotBeNull();
        verification.ShouldBe(new OwnershipVerificationRow("manual", registrant, officer, TestTenants.Acme.TenantId, Note));
        var tenantAudit = (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, officer, "vendor.ownership_verified", Ct)).ShouldHaveSingleItem();
        tenantAudit.SubjectId.ShouldBe(companyId.ToString());
        tenantAudit.Data.ShouldContain("\"method\": \"manual\"");
        var platformAudit = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, officer, "vendor.ownership_verified", Ct)).ShouldHaveSingleItem();
        platformAudit.SubjectId.ShouldBe(companyId.ToString());
        platformAudit.Data.ShouldContain("\"tenant\": \"acme\"");
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, officer, "vendor.approved", Ct)).ShouldHaveSingleItem();
        (await directory.GetOwnershipCheckAsync(companyId, Ct)).ShouldNotBeNull().Verified.ShouldBeTrue();
    }

    [Fact]
    public async Task Ownership_is_not_verified_without_a_current_clean_cr_certificate_or_a_note()
    {
        var (withoutCertificate, _) = await VendorAsync("No Certificate Co");
        // A certificate still waiting for its virus scan is no certificate an officer could read.
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, withoutCertificate, VendorDocumentTypes.CrCertificate, await DatabaseClock.ValidUntilAsync(db.OwnerConnectionString, Ct), "pending_scan", isCurrent: true, Ct);
        var (withCertificate, _) = await VendorWithCertificateAsync("Blank Note Co");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using var host = Host(new FakeVendorAccounts());
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
        var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();

        (await directory.GetOwnershipCheckAsync(withoutCertificate, Ct)).ShouldNotBeNull().HasCertificate.ShouldBeFalse();
        var noCertificate = await directory.ApproveAsync(withoutCertificate, officer, new OwnershipConfirmation(Note, false, CrLookupOutcome.Manual), Ct);
        noCertificate.Error.ShouldNotBeNull().Code.ShouldBe(CrOwnershipErrors.NoCertificate);

        foreach (var note in new[] { null, "   ", new string('x', 1001), "hidden‮text" })
        {
            var refused = await directory.ApproveAsync(withCertificate, officer, new OwnershipConfirmation(note, false, CrLookupOutcome.Manual), Ct);
            refused.Error.ShouldNotBeNull().Code.ShouldBe(CrOwnershipErrors.NoteRequired);
        }

        foreach (var companyId in new[] { withoutCertificate, withCertificate })
        {
            (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");
            (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_second_tenant_approves_a_verified_company_without_a_check_and_learns_only_the_method()
    {
        var (companyId, _) = await VendorWithCertificateAsync("Verified Once Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var acmeOfficer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        var betaOfficer = await StaffAsync(TestTenants.Beta, TenantRoles.ContractsOfficer);
        var accounts = new FakeVendorAccounts();
        await using var host = Host(accounts);
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: acmeOfficer))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>()
                .ApproveAsync(companyId, acmeOfficer, new OwnershipConfirmation(Note, false, CrLookupOutcome.Manual), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (var scope = host.ScopeFor(TestTenants.Beta, actingUserId: betaOfficer))
        {
            var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();
            var check = (await directory.GetOwnershipCheckAsync(companyId, Ct)).ShouldNotBeNull();
            check.Verified.ShouldBeTrue();
            check.VerifiedMethod.ShouldBe(OwnershipVerificationMethod.Manual);
            // Nothing about who verified it, or where: no registrant, no lookup, and the contract has no note or tenant.
            check.Registrant.ShouldBeNull();
            check.Lookup.ShouldBeNull();

            (await directory.ApproveAsync(companyId, betaOfficer, Ct)).IsSuccess.ShouldBeTrue();
        }

        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Beta.TenantId].ShouldBe("approved");
        // Beta's session cannot read the verification row (no grant), and the function tells it the check was not its own.
        await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Beta.TenantId, null, betaOfficer, Ct);
        await using (var direct = new NpgsqlCommand("select note from vendor.ownership_verifications", session))
        {
            (await Should.ThrowAsync<PostgresException>(() => direct.ExecuteScalarAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var viaFunction = new NpgsqlCommand("select verified_here from vendor.related_ownership(@company)", session);
        viaFunction.Parameters.AddWithValue("company", companyId);
        ((bool)(await viaFunction.ExecuteScalarAsync(Ct))!).ShouldBeFalse();
        accounts.Steps.ShouldNotContain("profile");
    }

    [Fact]
    public async Task With_wathq_selected_the_officer_sees_the_crs_owners_and_managers_and_the_check_is_recorded_as_wathq()
    {
        var (companyId, registrant) = await VendorWithCertificateAsync("Wathq Found Co");
        var crNumber = await CrNumberAsync(companyId);
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        var requests = new ConcurrentQueue<(string Path, string? Key, string? Language)>();
        await using var wathq = await FakeHttpServer.StartAsync(async context =>
        {
            requests.Enqueue((context.Request.Path.Value!, context.Request.Headers["apiKey"].ToString(), context.Request.Query["language"].ToString()));
            context.Response.ContentType = "application/json";
            var body = context.Request.Path.Value!.EndsWith("/owners/" + crNumber, StringComparison.Ordinal)
                ? """[{"name":"Registered Person","typeId":11,"typeName":"Partner","identity":{"id":"1101552388","typeId":1,"typeName":"National ID"},"partnership":[{"id":8,"name":"Partner"}],"nationality":{"id":113,"name":"Saudi"}}]"""
                : """[{"name":"Hired Manager","typeId":1,"typeName":"Manager","isLicensed":true,"identity":{"id":"2202662499","typeId":1,"typeName":"National ID"},"nationality":{"id":113,"name":"Saudi"},"positions":[{"id":3,"name":"General manager"}]}]""";
            await context.Response.WriteAsync(body, context.RequestAborted);
        }, Ct);
        await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "wathq", Ct);
        try
        {
            await using var host = Host(new FakeVendorAccounts(), wathq: (wathq.BaseAddress + "sandbox/commercial-registration", "test-wathq-key"));
            await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
            var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();

            var lookup = (await directory.GetOwnershipCheckAsync(companyId, Ct)).ShouldNotBeNull().Lookup.ShouldNotBeNull();

            lookup.Outcome.ShouldBe(CrLookupOutcome.Found);
            var owner = lookup.Owners.ShouldHaveSingleItem();
            (owner.Name, owner.Type, string.Join('|', owner.Positions)).ShouldBe(("Registered Person", "Partner", "Partner"));
            var manager = lookup.Managers.ShouldHaveSingleItem();
            (manager.Name, manager.Type, string.Join('|', manager.Positions)).ShouldBe(("Hired Manager", "Manager", "General manager"));
            requests.Select(r => r.Path).Order(StringComparer.Ordinal).ShouldBe(
                [$"/sandbox/commercial-registration/managers/{crNumber}", $"/sandbox/commercial-registration/owners/{crNumber}"]);
            requests.ShouldAllBe(r => r.Key == "test-wathq-key" && r.Language == "en");

            var result = await directory.ApproveAsync(companyId, officer, new OwnershipConfirmation(Note, BasedOnWathq: true, CrLookupOutcome.Found), Ct);

            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldNotBeNull().Method.ShouldBe("wathq");
            (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct))!.RegistrantUserId.ShouldBe(registrant);
            var audit = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, officer, "vendor.ownership_verified", Ct)).ShouldHaveSingleItem();
            audit.Data.ShouldContain("\"wathq_outcome\": \"found\"");
            audit.Data.ShouldNotContain("test-wathq-key");
        }
        finally
        {
            await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "manual", Ct);
        }
    }

    [Theory]
    [InlineData("not-configured", CrLookupOutcome.NotConfigured)]
    [InlineData("server-error", CrLookupOutcome.Unavailable)]
    [InlineData("unreachable", CrLookupOutcome.Unavailable)]
    [InlineData("not-json", CrLookupOutcome.Unavailable)]
    [InlineData("no-record", CrLookupOutcome.NotFound)]
    public async Task With_wathq_selected_but_failing_the_manual_check_applies_and_says_why(string failure, CrLookupOutcome expected)
    {
        var (companyId, _) = await VendorWithCertificateAsync($"Wathq {failure} Co");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using var wathq = await FakeHttpServer.StartAsync(async context =>
        {
            switch (failure)
            {
                case "server-error":
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await context.Response.WriteAsync("""{"code":"500.1.1","message":"Internal Server Error"}""", context.RequestAborted);
                    break;
                case "no-record":
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                default:
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("<html>not json</html>", context.RequestAborted);
                    break;
            }
        }, Ct);
        (string, string)? settings = failure switch
        {
            "not-configured" => null,
            "unreachable" => ("http://127.0.0.1:9/", "test-wathq-key"),
            _ => (wathq.BaseAddress, "test-wathq-key"),
        };
        await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "wathq", Ct);
        try
        {
            await using var host = Host(new FakeVendorAccounts(), wathq: settings);
            await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
            var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();

            var lookup = (await directory.GetOwnershipCheckAsync(companyId, Ct)).ShouldNotBeNull().Lookup.ShouldNotBeNull();

            lookup.Outcome.ShouldBe(expected);
            lookup.Owners.ShouldBeEmpty();
            // Without a confirmation nothing is approved; a Wathq-based confirmation is not what the officer saw, and the
            // manual confirmation is recorded as manual with the Wathq outcome in the audit.
            (await directory.ApproveAsync(companyId, officer, Ct)).Error.ShouldNotBeNull().Code.ShouldBe(CrOwnershipErrors.Unverified);
            var result = await directory.ApproveAsync(companyId, officer, new OwnershipConfirmation(Note, false, lookup.Outcome), Ct);
            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldNotBeNull().Method.ShouldBe("manual");
            var outcome = expected switch
            {
                CrLookupOutcome.NotConfigured => "not_configured",
                CrLookupOutcome.NotFound => "not_found",
                _ => "unavailable",
            };
            (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, officer, "vendor.ownership_verified", Ct))
                .ShouldHaveSingleItem().Data.ShouldContain($"\"wathq_outcome\": \"{outcome}\"");
        }
        finally
        {
            await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "manual", Ct);
        }
    }

    [Fact]
    public async Task A_wathq_check_is_refused_while_the_platform_uses_the_manual_method()
    {
        var (companyId, _) = await VendorWithCertificateAsync("Wathq Not Selected Co");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using var host = Host(new FakeVendorAccounts());
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);

        var refused = await scope.ServiceProvider.GetRequiredService<IVendorDirectory>()
            .ApproveAsync(companyId, officer, new OwnershipConfirmation(Note, BasedOnWathq: true, CrLookupOutcome.Found), Ct);

        refused.Error.ShouldNotBeNull().Code.ShouldBe(CrOwnershipErrors.WathqNotSelected);
        (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task When_the_identity_provider_does_not_answer_the_registrant_is_unknown_but_the_check_still_shows()
    {
        var (companyId, registrant) = await VendorWithCertificateAsync("Profile Unknown Co");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using var host = Host(new FakeVendorAccounts { FailProfile = true });
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);

        var check = (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().GetOwnershipCheckAsync(companyId, Ct)).ShouldNotBeNull();

        check.Registrant.ShouldBe(new VendorRegistrant(registrant, null, null));
    }

    [Fact]
    public async Task Staff_of_another_role_or_a_vendor_session_cannot_verify_ownership_in_the_database()
    {
        var (companyId, vendorUser) = await VendorWithCertificateAsync("Verify Callers Co");
        var evaluator = await StaffAsync(TestTenants.Acme, TenantRoles.TechnicalEvaluator);

        foreach (var (tenant, vendor, user) in new (Guid?, Guid?, string?)[]
                 {
                     (TestTenants.Acme.TenantId, null, evaluator),
                     (TestTenants.Acme.TenantId, companyId, vendorUser),
                     (null, null, "platform-admin-sub"),
                 })
        {
            await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, vendor, user, Ct);
            await using var command = new NpgsqlCommand("select vendor.verify_ownership(@company, 'manual', 'A note.')", session);
            command.Parameters.AddWithValue("company", companyId);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeNull();
    }

    private ModuleHost Host(FakeVendorAccounts accounts, (string BaseUrl, string ApiKey)? wathq = null) =>
        new(db.AppConnectionString, configure: services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            services.Configure<WathqOptions>(options =>
            {
                options.BaseUrl = wathq?.BaseUrl;
                options.ApiKey = wathq?.ApiKey;
                options.TimeoutSeconds = 5;
            });
        });

    private async Task<(Guid CompanyId, string UserId)> VendorAsync(string nameEn)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        return (companyId, userId);
    }

    private async Task<(Guid CompanyId, string UserId)> VendorWithCertificateAsync(string nameEn)
    {
        var vendor = await VendorAsync(nameEn);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, vendor.CompanyId, VendorDocumentTypes.CrCertificate, await DatabaseClock.ValidUntilAsync(db.OwnerConnectionString, Ct), "clean", isCurrent: true, Ct);
        return vendor;
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
