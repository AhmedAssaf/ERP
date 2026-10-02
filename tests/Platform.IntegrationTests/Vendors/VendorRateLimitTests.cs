using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.RateLimiting;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// W-37 and W-35 through the real services: a join over the per-user or per-tenant limit, and a consent grant or
/// revocation over the per-company limit, is refused with the module's error before anything happens (no Keycloak call,
/// no relationship or ledger row, no audit entry); another user, tenant or company is not affected; once the window has
/// passed the same call is allowed again. The limits are lowered through <see cref="VendorsOptions"/> and the host's clock
/// moves on demand.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorRateLimitTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_users_join_over_its_limit_is_refused_before_keycloak_and_the_database_and_allowed_after_the_minute()
    {
        var (companyId, userId) = await VendorAsync("Per User Limited Joiner");
        var (otherCompanyId, otherUserId) = await VendorAsync("Unlimited Neighbour Joiner");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: [TestTenants.Acme.KeycloakOrgAlias]) };
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var host = Host(accounts, clock, o => o.JoinsPerUserPerMinute = 2);

        // Two joins of the tenant the company already works with: each asks Keycloak, neither changes anything.
        (await JoinAsync(host, TestTenants.Acme, companyId, userId)).IsSuccess.ShouldBeTrue();
        (await JoinAsync(host, TestTenants.Acme, companyId, userId)).IsSuccess.ShouldBeTrue();
        accounts.Steps.ShouldBe(["describe", "describe"]);

        var refused = await JoinAsync(host, TestTenants.Beta, companyId, userId);

        refused.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinRateLimited);
        refused.Error.Kind.ShouldBe(ErrorKind.Refused);
        accounts.Steps.ShouldBe(["describe", "describe"]); // a refused join never reaches Keycloak
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).Keys.ShouldBe([TestTenants.Acme.TenantId]);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).ShouldBeEmpty();

        // Another user of the same tenant is not affected.
        (await JoinAsync(host, TestTenants.Beta, otherCompanyId, otherUserId)).Value.RelationshipCreated.ShouldBeTrue();

        clock.Advance(TimeSpan.FromMinutes(1));
        var joined = await JoinAsync(host, TestTenants.Beta, companyId, userId);

        joined.IsSuccess.ShouldBeTrue(joined.Error?.Message);
        joined.Value.RelationshipCreated.ShouldBeTrue();
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Beta.TenantId].ShouldBe("pending");
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_tenants_joins_over_its_limit_are_refused_for_every_user_while_another_tenant_still_takes_them()
    {
        var first = await VendorAsync("Tenant Limit First Joiner");
        var second = await VendorAsync("Tenant Limit Second Joiner");
        var third = await VendorAsync("Tenant Limit Third Joiner");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: [TestTenants.Acme.KeycloakOrgAlias]) };
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var host = Host(accounts, clock, o => o.JoinsPerTenantPerMinute = 2);

        (await JoinAsync(host, TestTenants.Beta, first.CompanyId, first.UserId)).IsSuccess.ShouldBeTrue();
        (await JoinAsync(host, TestTenants.Beta, second.CompanyId, second.UserId)).IsSuccess.ShouldBeTrue();

        var refused = await JoinAsync(host, TestTenants.Beta, third.CompanyId, third.UserId);

        refused.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinRateLimited);
        accounts.Steps.ShouldBe(["add-organization", "add-organization"]); // the third join never reached Keycloak
        accounts.Revoked.ShouldBeEmpty();
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, third.CompanyId, Ct)).Keys.ShouldBe([TestTenants.Acme.TenantId]);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, third.UserId, "vendor.joined", Ct)).ShouldBeEmpty();

        // Acme's count is its own: the same user's join there still runs.
        (await JoinAsync(host, TestTenants.Acme, third.CompanyId, third.UserId)).IsSuccess.ShouldBeTrue();
        accounts.Steps.Last().ShouldBe("describe");

        clock.Advance(TimeSpan.FromMinutes(1));
        (await JoinAsync(host, TestTenants.Beta, third.CompanyId, third.UserId)).Value.RelationshipCreated.ShouldBeTrue();
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, third.UserId, "vendor.joined", Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_join_over_the_concurrent_cap_is_refused_without_a_keycloak_call_and_a_later_one_proceeds_once_a_slot_is_free()
    {
        var first = await VendorAsync("Concurrent Cap First Joiner");
        var second = await VendorAsync("Concurrent Cap Second Joiner");
        var third = await VendorAsync("Concurrent Cap Third Joiner");
        var inKeycloak = 0;
        var bothInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keycloakAnswers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accounts = new FakeVendorAccounts
        {
            State = new(HoldsVendorRole: true, OrganizationAliases: [TestTenants.Acme.KeycloakOrgAlias]),
            // A Keycloak brownout: the add hangs until the test lets it answer.
            OnAddOrganization = async _ =>
            {
                if (Interlocked.Increment(ref inKeycloak) == 2)
                {
                    bothInside.TrySetResult();
                }

                await keycloakAnswers.Task;
            },
        };
        await using var host = Host(accounts, new ManualClock(DateTimeOffset.UtcNow), o => o.MaxConcurrentJoins = 2);

        var firstJoin = Task.Run(() => JoinAsync(host, TestTenants.Beta, first.CompanyId, first.UserId), Ct);
        var secondJoin = Task.Run(() => JoinAsync(host, TestTenants.Beta, second.CompanyId, second.UserId), Ct);
        try
        {
            await bothInside.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // Without the cap the third join would hang in Keycloak with the other two; the wait turns that into a failure.
            var refused = await JoinAsync(host, TestTenants.Beta, third.CompanyId, third.UserId).WaitAsync(TimeSpan.FromSeconds(30), Ct);

            refused.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinBusy);
            accounts.Steps.Count(s => s == "add-organization").ShouldBe(2); // the third join never reached Keycloak
            (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, third.CompanyId, Ct)).Keys.ShouldBe([TestTenants.Acme.TenantId]);
            (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, third.UserId, "vendor.joined", Ct)).ShouldBeEmpty();
        }
        finally
        {
            keycloakAnswers.TrySetResult();
        }

        (await firstJoin).Value.RelationshipCreated.ShouldBeTrue();
        (await secondJoin).Value.RelationshipCreated.ShouldBeTrue();

        var joined = await JoinAsync(host, TestTenants.Beta, third.CompanyId, third.UserId);

        joined.IsSuccess.ShouldBeTrue(joined.Error?.Message);
        joined.Value.RelationshipCreated.ShouldBeTrue();
        accounts.Steps.Count(s => s == "add-organization").ShouldBe(3);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, third.UserId, "vendor.joined", Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_companys_grant_over_its_limit_is_refused_with_no_row_and_no_audit_but_a_revocation_always_goes_through()
    {
        var today = await DatabaseTodayAsync();
        // Nine in the morning in Riyadh on the day after the database's today, so no grant below is ever backdated and the
        // hour the test moves on stays the same day.
        var clock = new ManualClock(new DateTimeOffset(today.AddDays(1), new TimeOnly(9, 0), TimeSpan.FromHours(3)));
        var from = today.AddDays(1);
        var (companyId, userId) = await VendorAsync("Consent Limited Company");
        var (otherCompanyId, otherUserId) = await VendorAsync("Consent Unlimited Company");
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, "Recipient of the consent limit", Ct);
        await using var host = Host(new FakeVendorAccounts(), clock, o => o.ConsentGrantsPerCompanyPerHour = 2);

        // A refused period never reaches the limit: only a grant that would be written counts.
        (await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, from, from.AddDays(-1), userId, Ct)))
            .Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.InvalidPeriod);
        var firstGrant = (await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, from, from.AddYears(1), userId, Ct))).Value;
        var secondGrant = (await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.PoRecords, from, from.AddYears(1), userId, Ct))).Value;

        var refusedGrant = await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.ProfileDocuments, from, from.AddYears(1), userId, Ct));

        refusedGrant.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.RateLimited);
        refusedGrant.Error.Kind.ShouldBe(ErrorKind.Refused);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Count.ShouldBe(2);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_granted", Ct)).Count.ShouldBe(2);

        // PDPL: consent can be withdrawn at any time, so with the grant limit reached both revocations still go through,
        // each recorded and audited; and they give no grant back.
        (await LedgerAsync(host, companyId, userId, l => l.RevokeAsync(firstGrant, userId, Ct))).IsSuccess.ShouldBeTrue();
        (await LedgerAsync(host, companyId, userId, l => l.RevokeAsync(secondGrant, userId, Ct))).IsSuccess.ShouldBeTrue();
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Count(r => r.Kind == "revoke").ShouldBe(2);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_revoked", Ct)).Count.ShouldBe(2);
        (await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.ProfileDocuments, from, from.AddYears(1), userId, Ct)))
            .Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.RateLimited);

        // Another company's admin is not affected.
        (await LedgerAsync(host, otherCompanyId, otherUserId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, from, from.AddYears(1), otherUserId, Ct)))
            .IsSuccess.ShouldBeTrue();

        clock.Advance(TimeSpan.FromMinutes(59));
        (await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.ProfileDocuments, from, from.AddYears(1), userId, Ct)))
            .Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.RateLimited);
        clock.Advance(TimeSpan.FromMinutes(1));
        (await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.ProfileDocuments, from, from.AddYears(1), userId, Ct)))
            .IsSuccess.ShouldBeTrue();
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_granted", Ct)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Related_vendors_posting_the_join_again_do_not_use_up_the_tenants_limit_for_a_new_vendor()
    {
        var related = new List<(Guid CompanyId, string UserId)>();
        for (var i = 0; i < 4; i++)
        {
            related.Add(await VendorAsync($"Already Related Re-poster {i}"));
        }

        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: [TestTenants.Acme.KeycloakOrgAlias]) };
        await using var host = Host(accounts, new ManualClock(DateTimeOffset.UtcNow), o => o.JoinsPerTenantPerMinute = 2);

        // Each related vendor re-posts on acme up to its own limit (five a minute): twenty joins of acme in the minute.
        foreach (var (companyId, userId) in related)
        {
            for (var i = 0; i < new VendorsOptions().JoinsPerUserPerMinute; i++)
            {
                (await JoinAsync(host, TestTenants.Acme, companyId, userId)).IsSuccess.ShouldBeTrue();
            }
        }

        // A vendor new to acme (its relationship removed, as if it had registered elsewhere) still joins acme.
        var newcomer = await VendorAsync("First Time Joiner Of Acme");
        await VendorRows.UnrelateAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, newcomer.CompanyId, Ct);

        var joined = await JoinAsync(host, TestTenants.Acme, newcomer.CompanyId, newcomer.UserId);

        joined.IsSuccess.ShouldBeTrue(joined.Error?.Message);
        joined.Value.RelationshipCreated.ShouldBeTrue("the re-posts on acme never counted against acme's first-time joins");
    }

    [Fact]
    public async Task A_join_refused_as_busy_says_so_and_does_not_use_up_the_tenants_limit()
    {
        var waiting = await VendorAsync("Busy Gate Joiner");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: [TestTenants.Acme.KeycloakOrgAlias]) };
        await using var host = Host(accounts, new ManualClock(DateTimeOffset.UtcNow), o =>
        {
            o.MaxConcurrentJoins = 1;
            o.JoinsPerTenantPerMinute = 1;
        });
        var gate = host.Services.GetRequiredService<ConcurrentJoinGate>();
        var held = (await gate.TryEnterAsync(TestTenants.Beta.TenantId, "someone-else", Ct)).ShouldNotBeNull();

        var busy = await JoinAsync(host, TestTenants.Beta, waiting.CompanyId, waiting.UserId);

        busy.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinBusy);
        busy.Error.Kind.ShouldBe(ErrorKind.Refused);
        accounts.Steps.ShouldBeEmpty();
        held.Dispose();

        // Beta's one join of the minute is still there.
        (await JoinAsync(host, TestTenants.Beta, waiting.CompanyId, waiting.UserId)).Value.RelationshipCreated.ShouldBeTrue();
    }

    [Fact]
    public async Task A_join_that_keycloak_refuses_gives_its_slot_back()
    {
        var (failedCompany, failedUser) = await VendorAsync("Keycloak Refused Slot Joiner");
        var (nextCompany, nextUser) = await VendorAsync("After Keycloak Refusal Joiner");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: [TestTenants.Acme.KeycloakOrgAlias]), FailOrganization = true };
        await using var host = Host(accounts, new ManualClock(DateTimeOffset.UtcNow), o => o.MaxConcurrentJoins = 1);

        (await JoinAsync(host, TestTenants.Beta, failedCompany, failedUser)).Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinFailed);

        await NextJoinProceedsAsync(host, accounts, nextCompany, nextUser);
    }

    [Fact]
    public async Task A_join_whose_database_step_fails_gives_its_slot_back_after_the_undo()
    {
        var (failedCompany, failedUser) = await VendorAsync("Undone Slot Joiner");
        var (nextCompany, nextUser) = await VendorAsync("After Undo Joiner");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: [TestTenants.Acme.KeycloakOrgAlias]) };
        var audit = new FailOnceAuditWriter();
        await using var host = Host(accounts, new ManualClock(DateTimeOffset.UtcNow), o => o.MaxConcurrentJoins = 1, s =>
        {
            var module = s.Single(d => d.ServiceType == typeof(Platform.Modules.Audit.Contracts.IAuditWriter)).ImplementationType!;
            s.Replace(ServiceDescriptor.Scoped<Platform.Modules.Audit.Contracts.IAuditWriter>(sp => audit.Over(sp, module)));
        });

        (await JoinAsync(host, TestTenants.Beta, failedCompany, failedUser)).Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinFailed);
        accounts.Revoked.ShouldHaveSingleItem().UserId.ShouldBe(failedUser);

        await NextJoinProceedsAsync(host, accounts, nextCompany, nextUser);
    }

    [Fact]
    public async Task A_join_whose_keycloak_call_throws_gives_its_slot_back()
    {
        var (failedCompany, failedUser) = await VendorAsync("Thrown Slot Joiner");
        var (nextCompany, nextUser) = await VendorAsync("After Throw Joiner");
        var throwOnce = 1;
        var accounts = new FakeVendorAccounts
        {
            State = new(HoldsVendorRole: true, OrganizationAliases: [TestTenants.Acme.KeycloakOrgAlias]),
            // Not an IdentityProviderException: it propagates out of the join.
            OnAddOrganization = _ => Interlocked.Exchange(ref throwOnce, 0) == 1
                ? throw new HttpRequestException("forced")
                : Task.CompletedTask,
        };
        await using var host = Host(accounts, new ManualClock(DateTimeOffset.UtcNow), o => o.MaxConcurrentJoins = 1);

        await Should.ThrowAsync<HttpRequestException>(() => JoinAsync(host, TestTenants.Beta, failedCompany, failedUser));

        await NextJoinProceedsAsync(host, accounts, nextCompany, nextUser);
    }

    private async Task NextJoinProceedsAsync(ModuleHost host, FakeVendorAccounts accounts, Guid companyId, string userId)
    {
        host.Services.GetRequiredService<ConcurrentJoinGate>().Available.ShouldBe(1);
        accounts.FailOrganization = false;
        var next = await JoinAsync(host, TestTenants.Beta, companyId, userId);
        next.IsSuccess.ShouldBeTrue(next.Error?.Message);
        next.Value.RelationshipCreated.ShouldBeTrue();
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).ShouldHaveSingleItem();
    }

    private ModuleHost Host(
        FakeVendorAccounts accounts, TimeProvider clock, Action<VendorsOptions> limits, Action<IServiceCollection>? configure = null) =>
        new(db.AppConnectionString, clock: clock, configure: s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            s.Configure(limits);
            configure?.Invoke(s);
        });

    private static async Task<Result<VendorJoined>> JoinAsync(ModuleHost host, TenantContext tenant, Guid companyId, string userId)
    {
        await using var scope = host.ScopeFor(tenant, companyId, userId);
        return await scope.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct);
    }

    private static async Task<T> LedgerAsync<T>(ModuleHost host, Guid companyId, string userId, Func<IConsentLedger, Task<T>> act)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        return await act(scope.ServiceProvider.GetRequiredService<IConsentLedger>());
    }

    private async Task<(Guid CompanyId, string UserId)> VendorAsync(string nameEn)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        return (companyId, userId);
    }

    private async Task<DateOnly> DatabaseTodayAsync()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select ((now() + interval '3 hours') at time zone 'UTC')::date", connection);
        return (DateOnly)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>
    /// The module's own audit writer, except that its first write fails as a database fault would, so the join's database
    /// step fails after its Keycloak add and the undo runs.
    /// </summary>
    private sealed class FailOnceAuditWriter
    {
        private int _failed;

        public Platform.Modules.Audit.Contracts.IAuditWriter Over(IServiceProvider services, Type module) =>
            new Writer(this, (Platform.Modules.Audit.Contracts.IAuditWriter)ActivatorUtilities.CreateInstance(services, module));

        private sealed class Writer(FailOnceAuditWriter owner, Platform.Modules.Audit.Contracts.IAuditWriter inner) : Platform.Modules.Audit.Contracts.IAuditWriter
        {
            public Task WriteAsync(Platform.Modules.Audit.Contracts.AuditEntry entry, CancellationToken cancellationToken = default) =>
                Interlocked.Exchange(ref owner._failed, 1) == 0
                    ? throw new Microsoft.EntityFrameworkCore.DbUpdateException("forced audit failure", new InvalidOperationException("forced"))
                    : inner.WriteAsync(entry, cancellationToken);
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
