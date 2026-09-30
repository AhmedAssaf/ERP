using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// W-33 review of PR #5: a retry of an uphold's identity provider update never undoes a tenant's own decision (W-21 P-1:
/// only the tenant restores access). It runs only the steps that failed, only for the tenants the company worked with when
/// the uphold committed, and nothing at all once the company has moved on to someone else. And <c>/vendor/join</c> says
/// "still giving you access" only while the uphold's add to that tenant's organization is really pending (migration 0024).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CrDisputeRetryTests(DatabaseFixture db)
{
    private const string Statement = "We are the owners named on the CR certificate; someone else registered our company.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Acme => TestTenants.Acme.KeycloakOrgAlias;

    private static string Beta => TestTenants.Beta.KeycloakOrgAlias;

    [Fact]
    public async Task A_retry_after_the_tenant_removed_the_claimant_does_not_put_them_back_in_its_organization()
    {
        var (companyId, squatter, crNumber) = await VendorAsync("Removed By Beta Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts { FailRevoke = true };
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse("the squatter's access was not taken back");
        accounts.OrganizationsOf(claimant).ShouldBe([Acme, Beta]);

        // Beta removes the claimant from its organization; then Keycloak answers again and the platform admin retries.
        accounts.Memberships.TryRemove((claimant, Beta), out _);
        accounts.FailRevoke = false;
        var addsBefore = accounts.Steps.Count(s => s == "add-organization");
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.Updated);

        accounts.OrganizationsOf(claimant).ShouldBe([Acme], "only beta restores the claimant's access to beta");
        accounts.Steps.Count(s => s == "add-organization").ShouldBe(addsBefore);
        accounts.Revoked.ShouldContain(g => g.UserId == squatter && g.RoleAdded && !g.OrganizationAdded);
    }

    [Fact]
    public async Task A_retry_of_an_earlier_dispute_after_a_later_one_moved_the_company_grants_nothing()
    {
        var (companyId, _, crNumber) = await VendorAsync("Moved Twice Co");
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts
        {
            OnGrantRole = _ => throw new IdentityProviderException("Keycloak did not grant the vendor role.", new HttpRequestException("forced")),
        };
        await using var host = Host(accounts);
        var firstDispute = await RaiseAsync(host, first, crNumber);
        (await UpholdAsync(host, firstDispute, admin)).IdentityProviderUpdated.ShouldBeFalse();

        // A later dispute moves the company on to a second claimant; its identity provider update succeeds.
        accounts.OnGrantRole = null;
        var secondDispute = await RaiseAsync(host, second, crNumber);
        (await UpholdAsync(host, secondDispute, admin)).IdentityProviderUpdated.ShouldBeTrue();
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(second, "vendor-admin")]);
        var grantsBefore = accounts.Granted.Count;
        var addsBefore = accounts.Steps.Count(s => s == "add-organization");
        var revokesBefore = accounts.Steps.Count(s => s == "revoke");

        // Retrying the first dispute now gives its claimant nothing, takes nothing from anyone, and takes it off the list.
        (await RetryAsync(host, firstDispute, admin)).ShouldBe(CrDisputeRetry.Superseded);

        accounts.Granted.Count.ShouldBe(grantsBefore);
        accounts.Steps.Count(s => s == "add-organization").ShouldBe(addsBefore);
        accounts.Steps.Count(s => s == "revoke").ShouldBe(revokesBefore);
        accounts.OrganizationsOf(first).ShouldBeEmpty();
        ((string)(await OwnerScalarAsync("select idp_outcome from vendor.cr_disputes where id = @id", firstDispute))!).ShouldBe("superseded");
        await using (var scope = host.PlatformScope(admin))
        {
            (await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().ListIdentityProviderFailuresAsync(Ct))
                .ShouldNotContain(f => f.DisputeId == firstDispute);
        }

        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_identity_provider", Ct))
            .Where(a => a.SubjectId == firstDispute.ToString()).Select(a => a.Data).ShouldContain(d => d.Contains("\"outcome\": \"superseded\""));
    }

    [Fact]
    public async Task A_retry_runs_only_the_steps_that_failed_and_keeps_the_done_ones()
    {
        var (companyId, squatter, crNumber) = await VendorAsync("Only Failed Steps Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        accounts.Memberships[(squatter, Acme)] = true;
        accounts.Memberships[(squatter, Beta)] = true;
        accounts.FailingOrganizations[Beta] = true;
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();

        accounts.FailingOrganizations.Clear();
        var before = accounts.Steps.ToArray().Length;
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.Updated);

        // Beta's add and the squatter's removal from beta were the failed steps; nothing else runs again.
        accounts.Steps.Skip(before).Order(StringComparer.Ordinal).ShouldBe(["add-organization", "revoke"]);
        accounts.OrganizationsOf(claimant).ShouldBe([Acme, Beta]);
        accounts.OrganizationsOf(squatter).ShouldBeEmpty();
        var details = (string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @id", disputeId))!;
        foreach (var step in new[] { "role:grant", $"organization:add:{Acme}", $"organization:add:{Beta}", $"organization:remove:{Acme}:{squatter}", $"organization:remove:{Beta}:{squatter}", $"role:revoke:{squatter}" })
        {
            details.ShouldContain($"\"{step}\": \"done\"");
        }

        ((string)(await OwnerScalarAsync("select idp_outcome from vendor.cr_disputes where id = @id", disputeId))!).ShouldBe("updated");
    }

    [Fact]
    public async Task A_retry_covers_only_the_tenants_the_company_worked_with_when_the_uphold_committed()
    {
        var (companyId, _, crNumber) = await VendorAsync("Related Later Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts { FailRevoke = true };
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();

        // After the uphold the claimant joins beta themselves (and beta may remove them); the uphold never covered beta.
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        accounts.FailRevoke = false;
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.Updated);

        accounts.OrganizationsOf(claimant).ShouldBe([Acme]);
        ((string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @id", disputeId))!).ShouldNotContain(Beta);
    }

    [Fact]
    public async Task A_member_removed_by_a_tenant_related_after_the_uphold_gets_the_audited_refusal_not_the_pending_retry()
    {
        var (companyId, _, crNumber) = await VendorAsync("Joined Beta Later Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeTrue();

        // The claimant later works with beta, and beta removes them from its organization.
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        accounts.State = new VendorAccountState(HoldsVendorRole: true, OrganizationAliases: [Acme]);

        await JoinRefusedAsync(host, companyId, claimant, VendorErrors.MembershipRemoved);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, claimant, "vendor.membership_restore_refused", Ct))
            .ShouldHaveSingleItem().SubjectId.ShouldBe(companyId.ToString());
    }

    [Fact]
    public async Task A_member_of_a_tenant_the_uphold_skipped_gets_the_audited_refusal_not_the_pending_retry()
    {
        var (companyId, _, crNumber) = await VendorAsync("Skipped Beta Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        var hidden = new HashSet<Guid> { TestTenants.Beta.TenantId };
        await using var host = new ModuleHost(db.AppConnectionString, configure: services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            var original = services.Last(d => d.ServiceType == typeof(ITenantCatalog));
            services.Replace(ServiceDescriptor.Scoped<ITenantCatalog>(sp =>
                new HidingCatalog((ITenantCatalog)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), hidden)));
        });
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeTrue("a tenant missing from the catalog is skipped, not failed");
        ((string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @id", disputeId))!)
            .ShouldContain($"organization:lookup:{TestTenants.Beta.TenantId:D}");

        hidden.Clear();
        accounts.State = new VendorAccountState(HoldsVendorRole: true, OrganizationAliases: [Acme]);

        await JoinRefusedAsync(host, companyId, claimant, VendorErrors.MembershipRemoved);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, claimant, "vendor.membership_restore_refused", Ct))
            .ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_pending_retry_answer_is_true_only_for_an_unrecorded_outcome_a_failed_add_or_a_failed_lookup()
    {
        var (companyId, _, crNumber) = await VendorAsync("Pending Answer Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts());
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        await UpholdAsync(host, disputeId, admin);

        var cases = new (string? Details, bool Pending)[]
        {
            (null, true),
            ($$"""{"role:grant": "done", "organization:add:{{Beta}}": "failed"}""", true),
            ("""{"role:grant": "done", "organization:lookup": "failed"}""", true),
            ($$"""{"role:grant": "done", "organization:add:{{Beta}}": "done"}""", false),
            ($$"""{"role:grant": "done", "organization:add:{{Acme}}": "failed"}""", false),
            ($$"""{"role:grant": "done", "organization:lookup:{{TestTenants.Beta.TenantId:D}}": "skipped: the tenant is not in the tenant catalog"}""", false),
            ("""{"role:grant": "done"}""", false),
        };
        foreach (var (details, pending) in cases)
        {
            await using (var owner = new NpgsqlConnection(db.OwnerConnectionString))
            {
                await owner.OpenAsync(Ct);
                await using var set = new NpgsqlCommand("""
                    update vendor.cr_disputes
                    set idp_details = @details::jsonb,
                        idp_outcome = case when @details is null then null else 'failed' end,
                        idp_recorded_at = case when @details is null then null else now() end
                    where id = @id
                    """, owner);
                set.Parameters.AddWithValue("details", NpgsqlTypes.NpgsqlDbType.Text, (object?)details ?? DBNull.Value);
                set.Parameters.AddWithValue("id", disputeId);
                await set.ExecuteNonQueryAsync(Ct);
            }

            await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Beta.TenantId, companyId, claimant, Ct);
            await using var ask = new NpgsqlCommand("select vendor.claimant_awaiting_organization(@alias)", session);
            ask.Parameters.AddWithValue("alias", Beta);
            ((bool)(await ask.ExecuteScalarAsync(Ct))!).ShouldBe(pending, details ?? "no recorded outcome");
        }
    }

    [Fact]
    public async Task A_superseded_retry_still_takes_the_squatters_role_and_memberships_when_its_removal_had_failed()
    {
        // Second review, B-1: the later uphold removes only the company's vendor users of its own time (the first
        // claimant); the squatter the first uphold removed has no row by then, so only the first dispute's retry can
        // still take the squatter's realm role and memberships.
        var (_, squatter, crNumber) = await VendorAsync("Squatter Kept Access Co");
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts { FailRevoke = true };
        accounts.Memberships[(squatter, Acme)] = true;
        await using var host = Host(accounts);
        var firstDispute = await RaiseAsync(host, first, crNumber);
        (await UpholdAsync(host, firstDispute, admin)).IdentityProviderUpdated.ShouldBeFalse("the squatter's access was not taken back");

        accounts.FailRevoke = false;
        var secondDispute = await RaiseAsync(host, second, crNumber);
        (await UpholdAsync(host, secondDispute, admin)).IdentityProviderUpdated.ShouldBeTrue();
        accounts.OrganizationsOf(squatter).ShouldBe([Acme], "the later uphold never touches the squatter");
        var grantsBefore = accounts.Granted.Count;
        var addsBefore = accounts.Steps.Count(s => s == "add-organization");

        // While Keycloak still fails, the superseded retry grants nothing, and the dispute stays listed.
        accounts.FailRevoke = true;
        (await RetryAsync(host, firstDispute, admin)).ShouldBe(CrDisputeRetry.StillFailing);
        ((string)(await OwnerScalarAsync("select idp_outcome from vendor.cr_disputes where id = @id", firstDispute))!).ShouldBe("failed");
        (await ListedAsync(host, admin)).ShouldContain(firstDispute);

        accounts.FailRevoke = false;
        (await RetryAsync(host, firstDispute, admin)).ShouldBe(CrDisputeRetry.Superseded);

        accounts.OrganizationsOf(squatter).ShouldBeEmpty();
        accounts.Revoked.ShouldContain(g => g.UserId == squatter && g.RoleAdded && !g.OrganizationAdded);
        accounts.Granted.Count.ShouldBe(grantsBefore, "the first claimant gets nothing back");
        accounts.Steps.Count(s => s == "add-organization").ShouldBe(addsBefore);
        accounts.OrganizationsOf(first).ShouldBeEmpty();
        accounts.OrganizationsOf(second).ShouldBe([Acme]);
        ((string)(await OwnerScalarAsync("select idp_outcome from vendor.cr_disputes where id = @id", firstDispute))!).ShouldBe("superseded");
        var details = (string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @id", firstDispute))!;
        details.ShouldContain($"\"role:revoke:{squatter}\": \"done\"");
        details.ShouldContain($"\"organization:remove:{Acme}:{squatter}\": \"done\"");
        (await ListedAsync(host, admin)).ShouldNotContain(firstDispute);
        var audits = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_identity_provider", Ct))
            .Where(a => a.SubjectId == firstDispute.ToString()).Select(a => a.Data).ToList();
        audits.ShouldContain(d => d.Contains("\"outcome\": \"superseded\"") && d.Contains($"\"role:revoke:{squatter}\": \"done\""));
    }

    [Fact]
    public async Task A_superseded_retry_leaves_a_removed_user_who_is_a_vendor_again_untouched()
    {
        // B-1: the squatter's removal failed, then the squatter won the company back with a dispute of their own.
        var (companyId, squatter, crNumber) = await VendorAsync("Squatter Won Back Co");
        var first = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts { FailRevoke = true };
        accounts.Memberships[(squatter, Acme)] = true;
        await using var host = Host(accounts);
        var firstDispute = await RaiseAsync(host, first, crNumber);
        (await UpholdAsync(host, firstDispute, admin)).IdentityProviderUpdated.ShouldBeFalse();

        accounts.FailRevoke = false;
        var secondDispute = await RaiseAsync(host, squatter, crNumber);
        (await UpholdAsync(host, secondDispute, admin)).IdentityProviderUpdated.ShouldBeTrue();
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(squatter, "vendor-admin")]);

        (await RetryAsync(host, firstDispute, admin)).ShouldBe(CrDisputeRetry.Superseded);

        accounts.Revoked.ShouldNotContain(g => g.UserId == squatter);
        accounts.OrganizationsOf(squatter).ShouldBe([Acme]);
        var details = (string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @id", firstDispute))!;
        details.ShouldContain($"\"role:revoke:{squatter}\": \"skipped: the user belongs to a vendor company again\"");
        details.ShouldContain($"\"organization:remove:{Acme}:{squatter}\": \"skipped: the user belongs to a vendor company again\"");
    }

    [Fact]
    public async Task A_retry_does_not_remove_a_former_squatter_who_now_belongs_to_another_company()
    {
        // Second review, m-3: the squatter's removal failed; they then register a company of their own with acme, whose
        // organization they hold again. A retry must not take it (W-21 would then refuse their /vendor/join).
        var (_, squatter, crNumber) = await VendorAsync("Squatter Registers Own Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts { FailRevoke = true };
        accounts.Memberships[(squatter, Acme)] = true;
        await using var host = Host(accounts);
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();

        await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, squatter, VendorRows.NewCrNumber(), "Squatters Own Co", Ct);
        accounts.FailRevoke = false;
        (await RetryAsync(host, disputeId, admin)).ShouldBe(CrDisputeRetry.Updated);

        accounts.Revoked.ShouldNotContain(g => g.UserId == squatter);
        accounts.OrganizationsOf(squatter).ShouldBe([Acme]);
        accounts.OrganizationsOf(claimant).ShouldBe([Acme]);
        var details = (string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @id", disputeId))!;
        details.ShouldContain($"\"role:revoke:{squatter}\": \"skipped: the user belongs to a vendor company again\"");
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_identity_provider", Ct))
            .Where(a => a.SubjectId == disputeId.ToString()).Select(a => a.Data)
            .ShouldContain(d => d.Contains("\"retry\": \"true\"") && d.Contains($"\"role:revoke:{squatter}\": \"skipped: the user belongs to a vendor company again\""));
    }

    [Fact]
    public async Task A_retry_takes_back_what_it_granted_when_a_later_uphold_moved_the_company_while_it_ran()
    {
        // Second review, m-1: the retry's check finds the claimant still the vendor admin; a later dispute is upheld while
        // the retry's Keycloak steps run (here: inside its role grant), and its removal of the first claimant lands before
        // the retry's grant and add. The retry checks again after its steps and takes back what it granted.
        var (_, squatter, crNumber) = await VendorAsync("Moved During Retry Co");
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts
        {
            OnGrantRole = u => u == first
                ? throw new IdentityProviderException("Keycloak did not grant the vendor role.", new HttpRequestException("forced"))
                : Task.CompletedTask,
        };
        accounts.FailingOrganizations[Acme] = true;
        await using var host = Host(accounts);
        var firstDispute = await RaiseAsync(host, first, crNumber);
        (await UpholdAsync(host, firstDispute, admin)).IdentityProviderUpdated.ShouldBeFalse();
        accounts.OrganizationsOf(first).ShouldBeEmpty();

        accounts.FailingOrganizations.Clear();
        var secondDispute = await RaiseAsync(host, second, crNumber);
        var moved = false;
        accounts.OnGrantRole = async u =>
        {
            if (u == first && !moved)
            {
                moved = true;
                (await UpholdAsync(host, secondDispute, admin)).IdentityProviderUpdated.ShouldBeTrue();
            }
        };

        (await RetryAsync(host, firstDispute, admin)).ShouldBe(CrDisputeRetry.Superseded);

        moved.ShouldBeTrue();
        accounts.OrganizationsOf(first).ShouldBeEmpty("the retry's add after the later uphold is taken back");
        accounts.Revoked.Count(g => g.UserId == first && g.RoleAdded).ShouldBe(2, "the later uphold's revoke, then the retry's own");
        accounts.OrganizationsOf(second).ShouldBe([Acme]);
        accounts.OrganizationsOf(squatter).ShouldBeEmpty();
        ((string)(await OwnerScalarAsync("select idp_outcome from vendor.cr_disputes where id = @id", firstDispute))!).ShouldBe("superseded");
        var details = (string)(await OwnerScalarAsync("select idp_details::text from vendor.cr_disputes where id = @id", firstDispute))!;
        details.ShouldContain("\"undo:role:grant\": \"done\"");
        details.ShouldContain($"\"undo:organization:add:{Acme}\": \"done\"");
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.dispute_identity_provider", Ct))
            .Where(a => a.SubjectId == firstDispute.ToString()).Select(a => a.Data)
            .ShouldContain(d => d.Contains("\"outcome\": \"superseded\"") && d.Contains("\"undo:role:grant\": \"done\""));
    }

    [Fact]
    public async Task Only_a_platform_console_session_checks_or_supersedes_a_disputes_identity_provider_update()
    {
        // Second review, m-4 (ADR-0012 point 4): a tenant or vendor session never reaches these functions.
        var (companyId, _, crNumber) = await VendorAsync("Supersede Caller Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host(new FakeVendorAccounts { FailRevoke = true });
        var disputeId = await RaiseAsync(host, claimant, crNumber);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();

        // Make the dispute one the function would mark: its claimant is no longer the company's vendor admin.
        await using (var owner = new NpgsqlConnection(db.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            await using var delete = new NpgsqlCommand("delete from vendor.vendor_users where user_id = @user", owner);
            delete.Parameters.AddWithValue("user", claimant);
            (await delete.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        foreach (var (tenant, vendor, user) in new (Guid?, Guid?, string?)[]
                 {
                     (TestTenants.Acme.TenantId, null, admin),
                     (TestTenants.Acme.TenantId, companyId, claimant),
                     (null, companyId, claimant),
                     (null, null, null),
                 })
        {
            foreach (var sql in new[] { "select vendor.supersede_dispute_idp(@id, '{}'::jsonb)", "select vendor.dispute_claimant_is_admin(@id)" })
            {
                await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, vendor, user, Ct);
#pragma warning disable CA2100 // One of two fixed statements.
                await using var command = new NpgsqlCommand(sql, session);
#pragma warning restore CA2100
                command.Parameters.AddWithValue("id", disputeId);
                (await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct)))
                    .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{sql} with tenant {tenant}, vendor {vendor}, user {user}");
            }
        }

        ((string)(await OwnerScalarAsync("select idp_outcome from vendor.cr_disputes where id = @id", disputeId))!).ShouldBe("failed");
    }

    private static async Task<IReadOnlyList<Guid>> ListedAsync(ModuleHost host, string admin)
    {
        await using var scope = host.PlatformScope(admin);
        return [.. (await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().ListIdentityProviderFailuresAsync(Ct)).Select(f => f.DisputeId)];
    }

    private static async Task JoinRefusedAsync(ModuleHost host, Guid companyId, string claimant, string code)
    {
        await using var scope = host.ScopeFor(TestTenants.Beta, vendorCompanyId: companyId, actingUserId: claimant);
        (await scope.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct)).Error.ShouldNotBeNull().Code.ShouldBe(code);
    }

    private static async Task<CrDisputeUpheld> UpholdAsync(ModuleHost host, Guid disputeId, string admin)
    {
        await using var scope = host.PlatformScope(admin);
        var upheld = await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(disputeId, "Checked.", admin, Ct);
        upheld.IsSuccess.ShouldBeTrue(upheld.Error?.Message);
        return upheld.Value;
    }

    private static async Task<CrDisputeRetry> RetryAsync(ModuleHost host, Guid disputeId, string admin)
    {
        await using var scope = host.PlatformScope(admin);
        return await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().RetryIdentityProviderAsync(disputeId, admin, Ct);
    }

    private ModuleHost Host(FakeVendorAccounts accounts) =>
        new(db.AppConnectionString, configure: services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

    private static async Task<Guid> RaiseAsync(ModuleHost host, string claimant, string crNumber)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
        var raised = await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
            new CrDisputeRequest(crNumber, Statement, VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English), $"{claimant}@example.test", "Claimant", Ct);
        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        return raised.Value;
    }

    private async Task<(Guid CompanyId, string UserId, string CrNumber)> VendorAsync(string nameEn)
    {
        var userId = Guid.NewGuid().ToString();
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, crNumber, nameEn, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, new DateOnly(2031, 1, 1), "clean", isCurrent: true, Ct);
        return (companyId, userId, crNumber);
    }

    private async Task<object?> OwnerScalarAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(Ct);
    }

    /// <summary>The real tenant catalog without the tenants in <paramref name="hidden"/> (a tenant not listed for a while).</summary>
    private sealed class HidingCatalog(ITenantCatalog inner, HashSet<Guid> hidden) : ITenantCatalog
    {
        public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            [.. (await inner.ListAsync(cancellationToken)).Where(t => !hidden.Contains(t.Id))];
    }
}
