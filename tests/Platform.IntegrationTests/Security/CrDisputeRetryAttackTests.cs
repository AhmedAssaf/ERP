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
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.Account;
using static Platform.IntegrationTests.Security.CrAttack;

namespace Platform.IntegrationTests.Security;

/// <summary>
/// Pentest of the W-33 post-review changes (migrations 0024 and 0025, the retry of an uphold's identity provider steps,
/// and the circuit's company check): the new functions from every session kind and a forged outcome; what a removed
/// squatter who registered a company of their own can still reach; and an open circuit whose company moved or whose
/// company check fails. Every test here is a regression guard; the open findings are reported separately.
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

    // Helpers --------------------------------------------------------------------------------------------------------

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

    private sealed class FixedNavigationManager : NavigationManager
    {
        public FixedNavigationManager() => Initialize("https://acme.localhost/", "https://acme.localhost/vendor");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
        }
    }
}
