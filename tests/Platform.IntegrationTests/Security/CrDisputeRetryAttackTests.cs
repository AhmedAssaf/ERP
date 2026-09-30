using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.Account;
using static Platform.IntegrationTests.Security.CrAttack;

namespace Platform.IntegrationTests.Security;

/// <summary>
/// Pentest of the W-33 post-review changes (migrations 0024 and 0025, the retry of an uphold's identity provider steps,
/// and the circuit's company check): the new functions from every session kind and a forged outcome; what a removed
/// squatter who registered a company of their own can still reach; and an open circuit whose company moved or whose
/// company check fails. Every test here is a regression guard. The R1 to R4 proofs were red on <c>ec49c97</c> and turned
/// green with migration 0026 and the standing check of each removal (the third review round of PR #5).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CrDisputeRetryAttackTests(DatabaseFixture db)
{
    private static string Acme => TestTenants.Acme.KeycloakOrgAlias;

    private static string Beta => TestTenants.Beta.KeycloakOrgAlias;

    // The functions of 0024 and 0025 ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("dispute_claimant_is_admin", "uuid")]
    [InlineData("supersede_dispute_idp", "uuid, jsonb")]
    [InlineData("claimant_awaiting_organization", "text")]
    [InlineData("dispute_related_tenants", "uuid")]
    [InlineData("upheld_disputes_needing_idp", "")]
    [InlineData("user_company_tenants", "text")]
    [InlineData("record_dispute_idp_outcome", "uuid, boolean, jsonb")]
    [InlineData("dispute_idp_outcome", "uuid")]
    public async Task The_retry_functions_run_as_their_owner_with_a_pinned_search_path_and_only_the_app_role_calls_them(string name, string arguments)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select p.prosecdef, p.proconfig::text, pg_get_function_identity_arguments(p.oid),
                   has_function_privilege('erp_app', p.oid, 'execute'),
                   has_function_privilege('public', p.oid, 'execute')
            from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'vendor' and p.proname = @name
            """, owner);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue($"vendor.{name} exists");
        reader.GetBoolean(0).ShouldBeTrue("security definer");
        reader.GetString(1).ShouldContain("search_path=vendor, pg_temp");
        System.Text.RegularExpressions.Regex.Replace(reader.GetString(2), @"p_\w+ ", string.Empty).ShouldBe(arguments);
        reader.GetBoolean(3).ShouldBeTrue("erp_app may execute it");
        reader.GetBoolean(4).ShouldBeFalse("public may not execute it");
        (await reader.ReadAsync(Ct)).ShouldBeFalse($"vendor.{name} has one signature (0025 dropped supersede_dispute_idp(uuid))");
    }

    [Fact]
    public async Task The_retry_listings_answer_no_session_but_the_platform_consoles()
    {
        var (companyId, squatter, crNumber) = await VendorAsync(db, "Listing Callers Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts { FailRevoke = true });
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();

        await using (var console = await OwnershipRows.AppSessionAsync(db.AppConnectionString, null, null, admin, Ct))
        {
            (await CountAsync(console, "select count(*)::int from vendor.dispute_related_tenants(@id)", disputeId)).ShouldBe(1, "the console sees acme");
            (await CountAsync(console, "select count(*)::int from vendor.upheld_disputes_needing_idp() where id = @id", disputeId)).ShouldBe(1);
        }

        foreach (var (tenant, vendor, user) in new (Guid?, Guid?, string?)[]
                 {
                     (TestTenants.Acme.TenantId, null, admin),
                     (TestTenants.Acme.TenantId, companyId, claimant),
                     (null, companyId, claimant),
                     (null, null, null),
                     (TestTenants.Acme.TenantId, companyId, squatter),
                 })
        {
            await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, vendor, user, Ct);
            (await CountAsync(session, "select count(*)::int from vendor.dispute_related_tenants(@id)", disputeId))
                .ShouldBe(0, $"dispute_related_tenants with tenant {tenant}, vendor {vendor}, user {user}");
            (await CountAsync(session, "select count(*)::int from vendor.upheld_disputes_needing_idp() where id = @id", disputeId))
                .ShouldBe(0, $"upheld_disputes_needing_idp with tenant {tenant}, vendor {vendor}, user {user}");
        }
    }

    [Fact]
    public async Task The_pending_retry_answer_is_the_claimants_own_in_their_own_companys_session_only()
    {
        // An outcome never recorded answers true for every alias; nobody but the claimant, in the company's vendor context,
        // gets that answer (a removed squatter would otherwise skip the audited refusal of /vendor/join).
        var (companyId, squatter, crNumber) = await VendorAsync(db, "Awaiting Callers Co");
        var (otherCompany, otherUser, _) = await VendorAsync(db, "Awaiting Other Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts());
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        await UpholdAsync(host, disputeId, admin);
        await OwnerScalarAsync(db, "update vendor.cr_disputes set idp_details = null, idp_outcome = null, idp_recorded_at = null where id = @id", ("id", disputeId));

        await using (var own = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Beta.TenantId, companyId, claimant, Ct))
        {
            (await AwaitingAsync(own)).ShouldBeTrue("the claimant's own answer");
        }

        foreach (var (tenant, vendor, user) in new (Guid?, Guid?, string?)[]
                 {
                     (TestTenants.Beta.TenantId, companyId, squatter),
                     (TestTenants.Beta.TenantId, otherCompany, claimant),
                     (TestTenants.Beta.TenantId, otherCompany, otherUser),
                     (TestTenants.Beta.TenantId, null, claimant),
                     (null, null, claimant),
                     (TestTenants.Beta.TenantId, companyId, null),
                 })
        {
            await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, vendor, user, Ct);
            (await AwaitingAsync(session)).ShouldBeFalse($"tenant {tenant}, vendor {vendor}, user {user}");
        }
    }

    [Fact]
    public async Task A_forged_superseded_outcome_does_not_close_a_dispute_whose_claimant_still_holds_the_company()
    {
        // supersede_dispute_idp takes the step outcomes from its caller; it may mark the dispute only while the claimant is
        // not the company's vendor admin, so a forged "everything done" cannot take a live dispute off the console's list.
        var (_, _, crNumber) = await VendorAsync(db, "Forged Supersede Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts { FailRevoke = true });
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();
        var before = (string)(await OwnerScalarAsync(db, "select idp_details::text from vendor.cr_disputes where id = @id", ("id", disputeId)))!;

        await using (var console = await OwnershipRows.AppSessionAsync(db.AppConnectionString, null, null, admin, Ct))
        await using (var forge = new NpgsqlCommand("""select vendor.supersede_dispute_idp(@id, '{"role:grant": "done"}'::jsonb)""", console))
        {
            forge.Parameters.AddWithValue("id", disputeId);
            ((bool)(await forge.ExecuteScalarAsync(Ct))!).ShouldBeFalse();
        }

        ((string)(await OwnerScalarAsync(db, "select idp_outcome from vendor.cr_disputes where id = @id", ("id", disputeId)))!).ShouldBe("failed");
        ((string)(await OwnerScalarAsync(db, "select idp_details::text from vendor.cr_disputes where id = @id", ("id", disputeId)))!).ShouldBe(before);
    }

    // A removed squatter who registered a company of their own ----------------------------------------------------

    [Fact]
    public async Task A_squatter_whose_removal_was_skipped_for_their_own_company_reaches_only_that_company()
    {
        // Attack 1: the squatter registers a throwaway company before the retry, so its removal steps are skipped for good
        // and it keeps the Keycloak side. Its vendor row is the throwaway company's, so every vendor context it gets is that
        // company's: the disputed company's rows stay out of reach under row-level security.
        var (companyId, squatter, crNumber) = await VendorAsync(db, "Skipped Squatter Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts { FailRevoke = true };
        accounts.Memberships[(squatter, Acme)] = true;
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();

        var throwaway = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, squatter, VendorRows.NewCrNumber(), "Throwaway Co", Ct);
        accounts.FailRevoke = false;
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.Updated);
        accounts.OrganizationsOf(squatter).ShouldContain(Acme, "precondition: the squatter's removal was skipped");

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: squatter))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorUsers>().FindCompanyAsync(squatter, Ct)).ShouldBe(throwaway);
        }

        // Even a session that still names the disputed company (a stale context) cannot act for it: the join refuses.
        await using (var stale = host.ScopeFor(TestTenants.Beta, vendorCompanyId: companyId, actingUserId: squatter))
        {
            await Should.ThrowAsync<InvalidOperationException>(() => stale.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct));
        }

        await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Acme.TenantId, throwaway, squatter, Ct);
        (await CountAsync(session, "select count(*)::int from vendor.documents where company_id = @id", companyId)).ShouldBe(0);
        (await CountAsync(session, "select count(*)::int from vendor.companies where id = @id", companyId)).ShouldBe(0);
        (await CountAsync(session, "select count(*)::int from vendor.vendor_users where company_id = @id", companyId)).ShouldBe(0);
        (await CountAsync(session, "select count(*)::int from vendor.consent_events where company_id = @id", companyId)).ShouldBe(0);
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).Keys.ShouldBe([TestTenants.Acme.TenantId]);
    }

    // The circuit's company check (f42cdab, 96f120d) -------------------------------------------------------------------

    [Fact]
    public async Task A_squatters_open_circuit_ends_when_its_user_now_belongs_to_a_company_of_their_own()
    {
        // The squatter registered a throwaway company after the uphold: the database answers a company, but not the one the
        // circuit holds.
        var (companyId, squatter, crNumber) = await VendorAsync(db, "Circuit Throwaway Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts { FailRevoke = true });
        await using var circuit = host.ScopeFor(TestTenants.Acme, vendorCompanyId: companyId, actingUserId: squatter);
        (await UpholdAsync(host, await RaiseAsync(host, claimant, crNumber), admin)).IdentityProviderUpdated.ShouldBeFalse();
        await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, squatter, VendorRows.NewCrNumber(), "Circuit Own Co", Ct);

        var guard = Guard();
        using var provider = Provider(guard, circuit.ServiceProvider.GetRequiredService<IVendorAccessor>(), circuit.ServiceProvider.GetRequiredService<IVendorUsers>());
        var state = await SignInAndWaitForSignOutAsync(provider, squatter);

        state.User.Identity?.IsAuthenticated.ShouldNotBe(true);
        guard.Ended.ShouldBeTrue();
    }

    [Fact]
    public async Task A_circuit_whose_company_check_fails_ends_rather_than_staying_open()
    {
        // Fail closed: the database cannot answer whose company it is, so the circuit's session ends (the base class stops
        // revalidating after an exception, so the guard must end it).
        var companyId = Guid.NewGuid();
        var vendors = new VendorAccessor();
        vendors.Set(new VendorContext(companyId));
        var guard = Guard();
        using var provider = Provider(guard, vendors, new FailingVendorUsers());

        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(VendorUser("circuit.failing"))));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!guard.Ended && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(20, Ct);
        }

        guard.Ended.ShouldBeTrue("a failed company check ends the circuit");
    }

    // Third round of PR #5: the retry pentest's proofs (R1 to R4), now guards -----------------------------------------

    [Fact]
    public async Task R1_a_retry_whose_tenant_lookup_fails_does_not_turn_the_failed_steps_into_permanent_skips()
    {
        // The uphold's add of the claimant to beta and the squatter's removal from beta fail. A retry then meets a transient
        // failure of the tenant lookup: it reaches none of the organization steps, and must keep the failed ones failed, not
        // mark them "skipped: no longer among the tenants of the uphold" (a final outcome). The next retry (lookup fine)
        // runs them: the verified owner gets beta and the squatter leaves it.
        var (companyId, squatter, crNumber) = await VendorAsync(db, "Lookup Hiccup Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        accounts.Memberships[(squatter, Acme)] = true;
        accounts.Memberships[(squatter, Beta)] = true;
        accounts.FailingOrganizations[Beta] = true;
        var catalog = new Switch();
        await using var host = new ModuleHost(db.AppConnectionString, configure: services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            var original = services.Last(d => d.ServiceType == typeof(ITenantCatalog));
            services.Replace(ServiceDescriptor.Scoped<ITenantCatalog>(sp =>
                new FlakyCatalog((ITenantCatalog)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), catalog)));
        });
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();

        accounts.FailingOrganizations.Clear();
        catalog.Fail = true;
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.StillFailing, "the lookup failed");
        catalog.Fail = false;
        var outcome = await RetryAsync(host, disputeId, admin);

        var details = (string)(await OwnerScalarAsync(db, "select idp_details::text from vendor.cr_disputes where id = @id", ("id", disputeId)))!;
        accounts.OrganizationsOf(claimant).ShouldContain(Beta, $"the verified owner never got beta; retry answered {outcome}, steps {details}");
        accounts.OrganizationsOf(squatter).ShouldNotContain(Beta, $"the squatter kept beta; retry answered {outcome}, steps {details}");
    }

    [Fact]
    public async Task R2_an_older_disputes_retry_does_not_put_its_claimant_back_into_an_organization_the_tenant_removed_them_from()
    {
        // X wins the company (dispute 1; the add to beta fails and is left for a retry), loses it to Y (dispute 2), and wins
        // it back (dispute 3; the add to beta works). Beta then removes X from its organization (W-21 P-1: only beta restores
        // access). Dispute 1 is superseded by the later upheld disputes (migration 0026), whoever holds the company now: its
        // retry gives X nothing, and X's /vendor/join on beta gets the audited refusal, not dispute 1's "pending retry".
        var (companyId, _, crNumber) = await VendorAsync(db, "Ping Pong Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var x = Guid.NewGuid().ToString();
        var y = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        accounts.FailingOrganizations[Beta] = true;
        await using var host = Host(accounts);
        var first = await RaiseAsync(host, x, crNumber);
        (await UpholdAsync(host, first, admin)).IdentityProviderUpdated.ShouldBeFalse();

        accounts.FailingOrganizations.Clear();
        (await UpholdAsync(host, await RaiseAsync(host, y, crNumber), admin)).IdentityProviderUpdated.ShouldBeTrue();
        (await UpholdAsync(host, await RaiseAsync(host, x, crNumber), admin)).IdentityProviderUpdated.ShouldBeTrue();
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(x, "vendor-admin")]);
        accounts.OrganizationsOf(x).ShouldBe([Acme, Beta]);

        // Beta removes X.
        accounts.Memberships.TryRemove((x, Beta), out _);
        accounts.State = new VendorAccountState(HoldsVendorRole: true, OrganizationAliases: [Acme]);
        string? joinCode;
        await using (var scope = host.ScopeFor(TestTenants.Beta, vendorCompanyId: companyId, actingUserId: x))
        {
            joinCode = (await scope.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct)).Error?.Code;
        }

        var retried = await RetryAsync(host, first, admin);

        var readded = accounts.OrganizationsOf(x).Contains(Beta);
        (readded, joinCode).ShouldBe(
            (false, VendorErrors.MembershipRemoved),
            $"dispute 1's retry ({retried}) put its claimant back into beta: {readded}; the join after beta's removal answered {joinCode}");
        retried.ShouldBe(CrDisputeRetry.Superseded);
    }

    [Fact]
    public async Task R3_a_squatter_with_a_throwaway_company_loses_the_disputed_companys_tenants_its_own_company_does_not_work_with()
    {
        // A removed user who belongs to a vendor company again keeps only the organizations of the tenants that company works
        // with. The squatter's own (throwaway) company works with acme only, so the retry takes beta's organization, which
        // came from the disputed company alone (ADR-0013 point 5: the removed users lose the memberships).
        var (companyId, squatter, crNumber) = await VendorAsync(db, "Throwaway Keeps Beta Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts { FailRevoke = true };
        accounts.Memberships[(squatter, Acme)] = true;
        accounts.Memberships[(squatter, Beta)] = true;
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();

        var throwaway = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, squatter, VendorRows.NewCrNumber(), "Throwaway Acme Only Co", Ct);
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, throwaway, Ct)).Keys.ShouldBe([TestTenants.Acme.TenantId]);
        accounts.FailRevoke = false;
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.Updated);

        accounts.OrganizationsOf(squatter).ShouldBe([Acme], "the squatter kept beta's organization, which only the disputed company gave it");
        accounts.Revoked.ShouldNotContain(g => g.UserId == squatter && g.RoleAdded, "the squatter is a vendor again and keeps the role");
        var details = (string)(await OwnerScalarAsync(db, "select idp_details::text from vendor.cr_disputes where id = @id", ("id", disputeId)))!;
        details.ShouldContain($"\"organization:remove:{Acme}:{squatter}\": \"skipped: the tenant works with the user's vendor company\"");
        details.ShouldContain($"\"organization:remove:{Beta}:{squatter}\": \"done\"");
        details.ShouldContain($"\"role:revoke:{squatter}\": \"skipped: the user belongs to a vendor company again\"");
    }

    [Fact]
    public async Task R4_a_retry_does_not_take_a_tenants_organization_from_a_removed_user_who_is_now_that_tenants_staff()
    {
        // The squatter's removal failed; beta then hires the person as staff (identity.members active, same Keycloak
        // organization). The retry skips beta's organization for them, with the reason, audited; the rest is taken.
        var (companyId, squatter, crNumber) = await VendorAsync(db, "Squatter Hired Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts { FailRevoke = true };
        accounts.Memberships[(squatter, Acme)] = true;
        accounts.Memberships[(squatter, Beta)] = true;
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();

        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Beta.TenantId, squatter, $"{squatter}@beta.test", [TenantRoles.ContractsOfficer], "active", Ct);
        accounts.FailRevoke = false;
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.Updated);

        accounts.OrganizationsOf(squatter).ShouldContain(Beta, "the retry removed beta's own staff member from beta's organization");
        accounts.OrganizationsOf(squatter).ShouldNotContain(Acme);
        accounts.Revoked.ShouldContain(g => g.UserId == squatter && g.RoleAdded, "a staff member does not keep the vendor role");
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_identity_provider", Ct))
            .Where(a => a.SubjectId == disputeId.ToString()).Select(a => a.Data)
            .ShouldContain(d => d.Contains("\"retry\": \"true\"") && d.Contains($"\"organization:remove:{Beta}:{squatter}\": \"skipped: the user is the tenant's staff\""));
    }

    [Fact]
    public async Task An_uphold_leaves_a_removed_user_in_the_organization_of_a_tenant_that_invited_them_as_staff()
    {
        // Follow-ups (a) and (b) of R4: the uphold's own removal (not only a retry) skips a tenant whose staff the removed
        // user is, and an invited (not yet active) member counts: StaffService added the membership when it invited them.
        var (companyId, squatter, crNumber) = await VendorAsync(db, "Squatter Invited Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Beta.TenantId, squatter, $"{squatter}@beta.test", [TenantRoles.TechnicalEvaluator], "invited", Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        accounts.Memberships[(squatter, Acme)] = true;
        accounts.Memberships[(squatter, Beta)] = true;
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);

        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeTrue("a skipped step is not a failure");

        accounts.OrganizationsOf(squatter).ShouldBe([Beta]);
        accounts.OrganizationsOf(claimant).ShouldBe([Acme, Beta]);
        var details = (string)(await OwnerScalarAsync(db, "select idp_details::text from vendor.cr_disputes where id = @id", ("id", disputeId)))!;
        details.ShouldContain($"\"organization:remove:{Beta}:{squatter}\": \"skipped: the user is the tenant's staff\"");
        details.ShouldContain($"\"role:revoke:{squatter}\": \"done\"");
    }

    [Fact]
    public async Task The_standing_lookups_of_a_removed_user_answer_only_a_platform_console_session()
    {
        // The company tenants of a user (vendor, 0026) and the tenants a user is staff of (identity, 0002) cross row-level
        // security as their owner; no tenant or vendor session reaches them.
        var (companyId, squatter, _) = await VendorAsync(db, "Standing Callers Co");
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Beta.TenantId, squatter, $"{squatter}@beta.test", [TenantRoles.ContractsOfficer], "active", Ct);
        const string companyTenants = "select count(*)::int from vendor.user_company_tenants(@user)";
        const string staffTenants = "select count(*)::int from identity.staff_tenants_of(@user)";

        await using (var console = await OwnershipRows.AppSessionAsync(db.AppConnectionString, null, null, admin, Ct))
        {
            (await UserCountAsync(console, companyTenants, squatter)).ShouldBe(1, "the console sees the company's acme relationship");
            (await UserCountAsync(console, staffTenants, squatter)).ShouldBe(1, "the console sees beta's member row");
        }

        foreach (var (tenant, vendor, user) in new (Guid?, Guid?, string?)[]
                 {
                     (TestTenants.Acme.TenantId, null, admin),
                     (TestTenants.Beta.TenantId, null, squatter),
                     (TestTenants.Acme.TenantId, companyId, squatter),
                     (null, companyId, squatter),
                     (null, null, null),
                 })
        {
            foreach (var sql in new[] { companyTenants, staffTenants })
            {
                await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, vendor, user, Ct);
                (await Should.ThrowAsync<PostgresException>(() => UserCountAsync(session, sql, squatter)))
                    .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{sql} with tenant {tenant}, vendor {vendor}, user {user}");
            }
        }

        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var check = new NpgsqlCommand("""
            select count(*)::int from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'identity' and p.proname = 'staff_tenants_of' and p.prosecdef
              and p.proconfig::text like '%search_path=identity, pg_temp%'
              and has_function_privilege('erp_app', p.oid, 'execute') and not has_function_privilege('public', p.oid, 'execute')
            """, owner);
        ((int)(await check.ExecuteScalarAsync(Ct))!).ShouldBe(1, "identity.staff_tenants_of: definer, pinned search_path, erp_app only");
    }

    // Helpers --------------------------------------------------------------------------------------------------------

    private static async Task<int> UserCountAsync(NpgsqlConnection session, string sql, string userId)
    {
#pragma warning disable CA2100 // Fixed statements of this class.
        await using var command = new NpgsqlCommand(sql, session);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("user", userId);
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    private ModuleHost Host(FakeVendorAccounts accounts) =>
        new(db.AppConnectionString, configure: services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

    private static async Task<Guid> RaiseAsync(ModuleHost host, string claimant, string crNumber)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
        var raised = await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), $"{claimant}@claimant.test", "Claimant", Ct);
        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        return raised.Value;
    }

    private static async Task<CrDisputeUpheld> UpholdAsync(ModuleHost host, Guid disputeId, string admin)
    {
        await using var scope = host.PlatformScope(admin);
        var upheld = await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(disputeId, OfficerNote, admin, Ct);
        upheld.IsSuccess.ShouldBeTrue(upheld.Error?.Message);
        return upheld.Value;
    }

    private static async Task<CrDisputeRetry> RetryAsync(ModuleHost host, Guid disputeId, string admin)
    {
        await using var scope = host.PlatformScope(admin);
        return await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().RetryIdentityProviderAsync(disputeId, admin, Ct);
    }

    private static async Task<int> CountAsync(NpgsqlConnection session, string sql, Guid id)
    {
#pragma warning disable CA2100 // Fixed statements of this class.
        await using var command = new NpgsqlCommand(sql, session);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("id", id);
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<bool> AwaitingAsync(NpgsqlConnection session)
    {
        await using var command = new NpgsqlCommand("select vendor.claimant_awaiting_organization(@alias)", session);
        command.Parameters.AddWithValue("alias", Beta);
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static ClaimsPrincipal VendorUser(string userId) =>
        new(new ClaimsIdentity([new Claim("sub", userId), new Claim("organization", "acme"), new Claim("roles", "vendor")], "test"));

    private static CircuitSessionGuard Guard() =>
        new(new FixedNavigationManager(), new HttpContextAccessor(), NullLogger<CircuitSessionGuard>.Instance);

    private static MembershipRevalidatingStateProvider Provider(CircuitSessionGuard guard, IVendorAccessor vendors, IVendorUsers users)
    {
        var tenants = new TenantAccessor();
        tenants.Set(TestTenants.Acme);
        return new MembershipRevalidatingStateProvider(
            NullLoggerFactory.Instance, new StillMember(), guard, tenants, new PlatformRequestContext(), vendors, users,
            TimeProvider.System, TimeSpan.FromMilliseconds(20));
    }

    private static async Task<AuthenticationState> SignInAndWaitForSignOutAsync(MembershipRevalidatingStateProvider provider, string userId)
    {
        var signedOut = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AuthenticationStateChanged += async task =>
        {
            var changed = await task;
            if (changed.User.Identity?.IsAuthenticated != true)
            {
                signedOut.TrySetResult(changed);
            }
        };
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(VendorUser(userId))));
        return await signedOut.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    /// <summary>Keycloak still lists the squatter in the organization (its removal failed or was skipped).</summary>
    private sealed class StillMember : IMembershipRevalidation
    {
        public Task<bool> IsStillMemberAsync(ClaimsPrincipal user, DateTimeOffset? signedInAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool ClaimsHostOrganization(ClaimsPrincipal user) => true;
    }

    private sealed class FailingVendorUsers : IVendorUsers
    {
        public Task<Guid?> FindCompanyAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The circuit's check must ask the database, not the scope's answers.");

        public Task<Guid?> FindCurrentCompanyAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("The database did not answer.");
    }

    private sealed class Switch
    {
        public volatile bool Fail;
    }

    /// <summary>The real tenant catalog, failing as a database hiccup would while <see cref="Switch.Fail"/> is set.</summary>
    private sealed class FlakyCatalog(ITenantCatalog inner, Switch state) : ITenantCatalog
    {
        public Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            state.Fail ? throw new InvalidOperationException("The tenant catalog could not be read.") : inner.ListAsync(cancellationToken);
    }

    private sealed class FixedNavigationManager : NavigationManager
    {
        public FixedNavigationManager() => Initialize("https://acme.localhost/", "https://acme.localhost/vendor");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
        }
    }
}
