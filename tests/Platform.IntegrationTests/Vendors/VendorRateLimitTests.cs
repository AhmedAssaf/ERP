using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
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
    public async Task A_companys_consent_change_over_its_limit_is_refused_with_no_row_and_no_audit_and_allowed_after_the_hour()
    {
        var today = await DatabaseTodayAsync();
        // Nine in the morning in Riyadh on the day after the database's today, so no grant below is ever backdated and the
        // hour the test moves on stays the same day.
        var clock = new ManualClock(new DateTimeOffset(today.AddDays(1), new TimeOnly(9, 0), TimeSpan.FromHours(3)));
        var from = today.AddDays(1);
        var (companyId, userId) = await VendorAsync("Consent Limited Company");
        var (otherCompanyId, otherUserId) = await VendorAsync("Consent Unlimited Company");
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, "Recipient of the consent limit", Ct);
        await using var host = Host(new FakeVendorAccounts(), clock, o => o.ConsentChangesPerCompanyPerHour = 2);

        // A refused period never reaches the limit: only a change that would be written counts.
        (await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, from, from.AddDays(-1), userId, Ct)))
            .Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.InvalidPeriod);
        var grantId = (await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, from, from.AddYears(1), userId, Ct))).Value;
        (await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.PoRecords, from, from.AddYears(1), userId, Ct))).IsSuccess.ShouldBeTrue();

        var refusedGrant = await LedgerAsync(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.ProfileDocuments, from, from.AddYears(1), userId, Ct));
        var refusedRevoke = await LedgerAsync(host, companyId, userId, l => l.RevokeAsync(grantId, userId, Ct));

        refusedGrant.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.RateLimited);
        refusedGrant.Error.Kind.ShouldBe(ErrorKind.Refused);
        refusedRevoke.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.RateLimited);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Count.ShouldBe(2);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_granted", Ct)).Count.ShouldBe(2);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_revoked", Ct)).ShouldBeEmpty();

        // Another company's admin is not affected.
        (await LedgerAsync(host, otherCompanyId, otherUserId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, from, from.AddYears(1), otherUserId, Ct)))
            .IsSuccess.ShouldBeTrue();

        clock.Advance(TimeSpan.FromMinutes(59));
        (await LedgerAsync(host, companyId, userId, l => l.RevokeAsync(grantId, userId, Ct))).Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.RateLimited);
        clock.Advance(TimeSpan.FromMinutes(1));
        (await LedgerAsync(host, companyId, userId, l => l.RevokeAsync(grantId, userId, Ct))).IsSuccess.ShouldBeTrue();
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Count.ShouldBe(3);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_revoked", Ct)).ShouldHaveSingleItem();
    }

    private ModuleHost Host(FakeVendorAccounts accounts, TimeProvider clock, Action<VendorsOptions> limits) =>
        new(db.AppConnectionString, clock: clock, configure: s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            s.Configure(limits);
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

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
