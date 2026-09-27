using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// The vendor consent ledger (vendor plan task 6, F-64, V-12, V-13, ADR-0010): a vendor admin grants consent per recipient,
/// scope and period and revokes it with a new row; nothing is updated or deleted; every grant, revocation and check is
/// audited in the platform audit, a check with the grant it relied on. A tenant never grants for a vendor.
/// <para>
/// Two clocks, on purpose. The application's clock is pinned to 1 January 2030 in Riyadh, always later than the database's
/// real date, so every grant the service accepts also passes the database's no-backdating rule, whatever day the suite runs.
/// A check always asks about the database's today (ADR-0010 point 2), so the check tests use grants written relative to
/// the database's own date (<see cref="ConsentRows.InsertGrantAsOwnerAsync"/>).
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ConsentLedgerTests(DatabaseFixture db)
{
    private static readonly DateTimeOffset AppNow = new(2030, 1, 1, 9, 0, 0, TimeSpan.FromHours(3));

    private static readonly DateOnly Today = DateOnly.FromDateTime(AppNow.DateTime);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_grant_is_recorded_and_audited()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Granting Company");
        await using var host = Host();

        var granted = await LedgerFor(host, companyId, userId, scope =>
            scope.GrantAsync(recipientId, ConsentScope.AwardRecords, Today, Today.AddYears(1), userId, Ct));

        var grantId = granted.IsSuccess ? granted.Value : throw new ShouldAssertException(granted.Error.Message);
        var row = (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();
        row.ShouldBe(new ConsentEventRow(grantId, recipientId, "award_records", "grant", Today, Today.AddYears(1), null, userId));
        var audit = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_granted", Ct)).ShouldHaveSingleItem();
        audit.SubjectType.ShouldBe("vendor_company");
        audit.SubjectId.ShouldBe(companyId.ToString());
        audit.Data.ShouldContain(grantId.ToString());
        audit.Data.ShouldContain("award_records");

        var listed = (await LedgerFor(host, companyId, userId, ledger => ledger.ListAsync(Ct))).ShouldHaveSingleItem();
        listed.Id.ShouldBe(grantId);
        listed.Recipient.Id.ShouldBe(recipientId);
        listed.Status.ShouldBe(ConsentStatus.Active);
        listed.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Revoking_adds_a_row_and_leaves_the_grant_unchanged()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Revoking Company");
        await using var host = Host();
        var grantId = (await LedgerFor(host, companyId, userId, ledger =>
            ledger.GrantAsync(recipientId, ConsentScope.PoRecords, Today, Today.AddMonths(6), userId, Ct))).Value;
        var before = (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();

        var revoked = await LedgerFor(host, companyId, userId, ledger => ledger.RevokeAsync(grantId, userId, Ct));

        var revocationId = revoked.IsSuccess ? revoked.Value : throw new ShouldAssertException(revoked.Error.Message);
        var rows = await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct);
        rows.Count.ShouldBe(2);
        rows.Single(r => r.Kind == "grant").ShouldBe(before);
        rows.Single(r => r.Kind == "revoke").ShouldBe(new ConsentEventRow(revocationId, recipientId, "po_records", "revoke", null, null, grantId, userId));
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_revoked", Ct))
            .ShouldHaveSingleItem().Data.ShouldContain(grantId.ToString());
        var listed = (await LedgerFor(host, companyId, userId, ledger => ledger.ListAsync(Ct))).ShouldHaveSingleItem();
        listed.Status.ShouldBe(ConsentStatus.Revoked);
        listed.RevokedBy.ShouldBe(userId);

        // A second revocation is refused and adds nothing.
        var again = await LedgerFor(host, companyId, userId, ledger => ledger.RevokeAsync(grantId, userId, Ct));
        again.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.AlreadyRevoked);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_revoked_grant_is_no_longer_in_force()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Revoked In Force Company");
        var grantId = await ConsentRows.InsertGrantAsOwnerAsync(db.OwnerConnectionString, companyId, recipientId, "po_records", -5, 30, userId, Ct);
        await using var host = Host();
        (await Check(host, companyId, recipientId, ConsentScope.PoRecords)).GrantId.ShouldBe(grantId);

        (await LedgerFor(host, companyId, userId, ledger => ledger.RevokeAsync(grantId, userId, Ct))).IsSuccess.ShouldBeTrue();

        (await Check(host, companyId, recipientId, ConsentScope.PoRecords)).ShouldBe(new ConsentCheckResult(Allowed: false, GrantId: null));
    }

    [Fact]
    public async Task A_check_without_an_active_grant_is_refused_and_audited()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Unconsented Company");
        // A grant in force today, but for another scope, does not count.
        await ConsentRows.InsertGrantAsOwnerAsync(db.OwnerConnectionString, companyId, recipientId, "profile_documents", 0, 365, userId, Ct);
        await using var host = Host();
        var operatorId = $"export-operator-{Guid.NewGuid():N}";

        var check = await Check(host, companyId, recipientId, ConsentScope.AwardRecords, operatorId);

        check.ShouldBe(new ConsentCheckResult(Allowed: false, GrantId: null));
        var audit = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, operatorId, "vendor.consent_check", Ct)).ShouldHaveSingleItem();
        audit.SubjectId.ShouldBe(companyId.ToString());
        audit.Data.ShouldContain("\"result\": \"refused\"");
        audit.Data.ShouldContain("award_records");
    }

    [Fact]
    public async Task A_check_with_an_active_grant_passes_and_records_the_grant_id()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Consented Company");
        var grantId = await ConsentRows.InsertGrantAsOwnerAsync(db.OwnerConnectionString, companyId, recipientId, "award_records", 0, 365, userId, Ct);
        await using var host = Host();
        var operatorId = $"export-operator-{Guid.NewGuid():N}";

        var check = await Check(host, companyId, recipientId, ConsentScope.AwardRecords, operatorId);

        check.ShouldBe(new ConsentCheckResult(Allowed: true, GrantId: grantId));
        var audit = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, operatorId, "vendor.consent_check", Ct)).ShouldHaveSingleItem();
        audit.Data.ShouldContain("\"result\": \"allowed\"");
        audit.Data.ShouldContain(grantId.ToString());
    }

    [Fact]
    public async Task A_check_today_counts_a_grant_from_its_first_through_its_last_day_and_not_before_or_after()
    {
        var (companyId, userId, _) = await VendorWithRecipientAsync("Period Edges Company");
        await using var host = Host();
        var cases = new (string Name, int From, int To, bool InForce)[]
        {
            ("starts tomorrow", 1, 30, false),
            ("starts today", 0, 30, true),
            ("ends today", -30, 0, true),
            ("ended yesterday", -30, -1, false),
        };

        foreach (var (name, from, to, inForce) in cases)
        {
            var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Edge {name}", Ct);
            var grantId = await ConsentRows.InsertGrantAsOwnerAsync(db.OwnerConnectionString, companyId, recipientId, "award_records", from, to, userId, Ct);

            var check = await Check(host, companyId, recipientId, ConsentScope.AwardRecords);

            check.ShouldBe(inForce ? new ConsentCheckResult(true, grantId) : new ConsentCheckResult(false, null), name);
        }
    }

    [Fact]
    public async Task The_listed_status_follows_the_period_on_the_applications_clock()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Listed Status Company");
        var from = Today.AddDays(10);
        var to = Today.AddDays(40);
        await using (var host = Host())
        {
            (await LedgerFor(host, companyId, userId, ledger =>
                ledger.GrantAsync(recipientId, ConsentScope.AwardRecords, from, to, userId, Ct))).IsSuccess.ShouldBeTrue();
        }

        foreach (var (day, expected) in new[]
        {
            (Today, ConsentStatus.NotYetValid), (from, ConsentStatus.Active), (to, ConsentStatus.Active), (to.AddDays(1), ConsentStatus.Expired),
        })
        {
            await using var host = Host(new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(3)));
            (await LedgerFor(host, companyId, userId, ledger => ledger.ListAsync(Ct))).ShouldHaveSingleItem().Status.ShouldBe(expected, day.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task A_grant_with_a_wrong_period_or_an_unknown_recipient_is_refused_and_records_nothing()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Refused Grant Company");
        await using var host = Host();

        foreach (var (from, to) in new[] { (Today, Today.AddDays(-1)), (Today.AddDays(-1), Today.AddDays(30)) })
        {
            var refused = await LedgerFor(host, companyId, userId, ledger => ledger.GrantAsync(recipientId, ConsentScope.AwardRecords, from, to, userId, Ct));
            refused.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.InvalidPeriod);
        }

        var unknown = await LedgerFor(host, companyId, userId, ledger =>
            ledger.GrantAsync(Guid.NewGuid(), ConsentScope.AwardRecords, Today, Today.AddDays(30), userId, Ct));
        unknown.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.UnknownRecipient);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_granted", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_start_the_database_finds_in_the_past_is_an_invalid_period_even_when_the_applications_clock_disagrees()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Slow Clock Company");
        // An application clock years behind the database's: the service's own check passes, the database's trigger refuses.
        var slow = new DateTimeOffset(2020, 1, 1, 9, 0, 0, TimeSpan.FromHours(3));
        var slowToday = DateOnly.FromDateTime(slow.DateTime);
        await using var host = Host(slow);

        var refused = await LedgerFor(host, companyId, userId, ledger =>
            ledger.GrantAsync(recipientId, ConsentScope.AwardRecords, slowToday, slowToday.AddYears(1), userId, Ct));

        refused.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.InvalidPeriod);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_granted", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_company_cannot_revoke_or_see_a_grant_and_its_vendor_cannot_check_it()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Owner Of The Grant");
        var (otherId, otherUser, _) = await VendorWithRecipientAsync("Other Company");
        await using var host = Host();
        var grantId = (await LedgerFor(host, companyId, userId, ledger =>
            ledger.GrantAsync(recipientId, ConsentScope.AwardRecords, Today, Today.AddYears(1), userId, Ct))).Value;

        var revoke = await LedgerFor(host, otherId, otherUser, ledger => ledger.RevokeAsync(grantId, otherUser, Ct));
        revoke.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.GrantNotFound);
        (await LedgerFor(host, otherId, otherUser, ledger => ledger.ListAsync(Ct))).ShouldBeEmpty();
        await using (var scope = host.ScopeFor(TestTenants.Acme, otherId, otherUser))
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<IConsentLedger>().CheckAsync(companyId, recipientId, ConsentScope.AwardRecords, Ct));
        }

        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_tenant_session_checks_only_a_company_its_tenant_works_with()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Related Check Company");
        var grantId = await ConsentRows.InsertGrantAsOwnerAsync(db.OwnerConnectionString, companyId, recipientId, "award_records", 0, 30, userId, Ct);
        await using var host = Host();

        await using (var acme = host.ScopeFor(TestTenants.Acme, actingUserId: $"acme-export-{Guid.NewGuid():N}"))
        {
            (await acme.ServiceProvider.GetRequiredService<IConsentLedger>().CheckAsync(companyId, recipientId, ConsentScope.AwardRecords, Ct))
                .GrantId.ShouldBe(grantId);
        }

        // Beta has no relationship with the company, so it learns nothing about its consents.
        await using var beta = host.ScopeFor(TestTenants.Beta, actingUserId: $"beta-export-{Guid.NewGuid():N}");
        await Should.ThrowAsync<InvalidOperationException>(() =>
            beta.ServiceProvider.GetRequiredService<IConsentLedger>().CheckAsync(companyId, recipientId, ConsentScope.AwardRecords, Ct));
    }

    [Fact]
    public async Task No_tenant_path_can_grant_consent_for_a_vendor()
    {
        var (companyId, _, recipientId) = await VendorWithRecipientAsync("Tenant Refused Company");
        var admin = $"admin-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, admin, $"{admin}@acme.test", [TenantRoles.TenantAdmin], "active", Ct);

        // The page: a staff principal is refused.
        await using (var factory = new PlatformWebFactory(db.AppConnectionString))
        {
            using var client = factory.ClientFor("acme.localhost");
            using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/consent").As(new TestUser(admin, ["acme"], "en")), Ct);
            page.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The service: a tenant scope without a vendor context is refused before anything is written.
        await using var host = Host();
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: admin))
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IConsentLedger>();
            await Should.ThrowAsync<InvalidOperationException>(() =>
                ledger.GrantAsync(recipientId, ConsentScope.AwardRecords, Today, Today.AddYears(1), admin, Ct));
            await Should.ThrowAsync<InvalidOperationException>(() => ledger.ListAsync(Ct));
        }

        // The database: a tenant connection cannot write a grant for the company (row-level security).
        await using (var connection = new NpgsqlConnection(db.AppConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("""
                select set_config('app.tenant_id', @tenant, false), set_config('app.user_id', @user, false);
                insert into vendor.consent_events (id, company_id, recipient_id, scope, kind, valid_from, valid_to, actor_id)
                values (gen_random_uuid(), @company, @recipient, 'award_records', 'grant',
                        ((now() + interval '3 hours') at time zone 'UTC')::date, ((now() + interval '3 hours') at time zone 'UTC')::date + 30, @user);
                """, connection);
            command.Parameters.AddWithValue("tenant", TestTenants.Acme.TenantId.ToString());
            command.Parameters.AddWithValue("user", admin);
            command.Parameters.AddWithValue("company", companyId);
            command.Parameters.AddWithValue("recipient", recipientId);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_acting_user_grants_or_revokes_as_themselves()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Actor Checked Company");
        await using var host = Host();

        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IConsentLedger>()
            .GrantAsync(recipientId, ConsentScope.AwardRecords, Today, Today.AddYears(1), "someone-else", Ct));
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_recipients_are_the_platform_list()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Recipient Reader");
        await using var host = Host();

        var recipients = await LedgerFor(host, companyId, userId, ledger => ledger.ListRecipientsAsync(Ct));

        recipients.ShouldContain(r => r.Id == recipientId && r.NameAr == "جهة مستلمة للاختبار" && r.NameEn == "Recipient of Recipient Reader");
    }

    [Fact]
    public async Task The_database_refuses_a_grant_by_another_actor_or_starting_before_today()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Database Checked Company");
        const string riyadhToday = "((now() + interval '3 hours') at time zone 'UTC')::date";

        // The session's own user, but another actor on the row.
        (await InsertGrantAsVendorAsync(companyId, userId, recipientId, actorId: "someone-else", validFrom: riyadhToday + " + 1"))
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        // Backdated: starts yesterday in Riyadh.
        (await InsertGrantAsVendorAsync(companyId, userId, recipientId, actorId: userId, validFrom: riyadhToday + " - 1"))
            .ShouldBe(PostgresErrorCodes.CheckViolation);
        // Control: today in Riyadh, by the session's user.
        (await InsertGrantAsVendorAsync(companyId, userId, recipientId, actorId: userId, validFrom: riyadhToday)).ShouldBeNull();
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty("every probe rolled back");
    }

    [Fact]
    public async Task Another_companys_vendor_session_is_refused_by_the_check_function()
    {
        var (companyId, _, recipientId) = await VendorWithRecipientAsync("Checked Company");
        var (otherId, otherUser, _) = await VendorWithRecipientAsync("Prying Company");

        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select set_config('app.tenant_id', @tenant, false), set_config('app.vendor_company_id', @vendor, false),
                   set_config('app.user_id', @user, false);
            select vendor.consent_grant_in_force(@company, @recipient, 'award_records');
            """, connection);
        command.Parameters.AddWithValue("tenant", TestTenants.Acme.TenantId.ToString());
        command.Parameters.AddWithValue("vendor", otherId.ToString());
        command.Parameters.AddWithValue("user", otherUser);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("recipient", recipientId);

        (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Two_concurrent_revocations_write_one_row_and_one_audit_entry()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Concurrent Revoker");
        await using var host = Host();
        var grantId = (await LedgerFor(host, companyId, userId, ledger =>
            ledger.GrantAsync(recipientId, ConsentScope.AwardRecords, Today, Today.AddYears(1), userId, Ct))).Value;

        var results = await Task.WhenAll(
            Task.Run(() => LedgerFor(host, companyId, userId, ledger => ledger.RevokeAsync(grantId, userId, Ct)), Ct),
            Task.Run(() => LedgerFor(host, companyId, userId, ledger => ledger.RevokeAsync(grantId, userId, Ct)), Ct));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Single(r => !r.IsSuccess).Error!.Code.ShouldBe(ConsentErrors.AlreadyRevoked);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Count(r => r.Kind == "revoke").ShouldBe(1);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_revoked", Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_consent_audits_name_the_host_tenant_when_there_is_one()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Tenant Named Company");
        await using var host = Host();
        var grantId = (await LedgerFor(host, companyId, userId, ledger =>
            ledger.GrantAsync(recipientId, ConsentScope.AwardRecords, Today, Today.AddYears(1), userId, Ct))).Value;
        (await LedgerFor(host, companyId, userId, ledger => ledger.RevokeAsync(grantId, userId, Ct))).IsSuccess.ShouldBeTrue();
        var acmeExport = $"acme-export-{Guid.NewGuid():N}";
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: acmeExport))
        {
            await scope.ServiceProvider.GetRequiredService<IConsentLedger>().CheckAsync(companyId, recipientId, ConsentScope.AwardRecords, Ct);
        }

        var acme = $"\"tenant_id\": \"{TestTenants.Acme.TenantId}\"";
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_granted", Ct)).ShouldHaveSingleItem().Data.ShouldContain(acme);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_revoked", Ct)).ShouldHaveSingleItem().Data.ShouldContain(acme);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, acmeExport, "vendor.consent_check", Ct)).ShouldHaveSingleItem().Data.ShouldContain(acme);

        // Without a host tenant (an export job) the entry carries a null tenant.
        var tenantless = $"tenantless-export-{Guid.NewGuid():N}";
        await Check(host, companyId, recipientId, ConsentScope.AwardRecords, tenantless);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, tenantless, "vendor.consent_check", Ct)).ShouldHaveSingleItem()
            .Data.ShouldContain("\"tenant_id\": null");
    }

    /// <summary>A grant inserted directly in the company's vendor session, rolled back; the SQL state it was refused with, or null.</summary>
    private async Task<string?> InsertGrantAsVendorAsync(Guid companyId, string sessionUser, Guid recipientId, string actorId, string validFrom)
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        try
        {
#pragma warning disable CA2100 // Test SQL built from constant date expressions only.
            await using var command = new NpgsqlCommand($"""
                select set_config('app.tenant_id', @tenant, true), set_config('app.vendor_company_id', @vendor, true),
                       set_config('app.user_id', @user, true);
                insert into vendor.consent_events (id, company_id, recipient_id, scope, kind, valid_from, valid_to, actor_id)
                values (gen_random_uuid(), @vendor::uuid, @recipient, 'award_records', 'grant', {validFrom}, current_date + 60, @actor);
                """, connection, transaction);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("tenant", TestTenants.Acme.TenantId.ToString());
            command.Parameters.AddWithValue("vendor", companyId.ToString());
            command.Parameters.AddWithValue("user", sessionUser);
            command.Parameters.AddWithValue("recipient", recipientId);
            command.Parameters.AddWithValue("actor", actorId);
            await command.ExecuteNonQueryAsync(Ct);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }
    }

    private ModuleHost Host(DateTimeOffset? now = null) =>
        new(db.AppConnectionString, clock: new FixedClock(now ?? AppNow));

    private static async Task<T> LedgerFor<T>(ModuleHost host, Guid companyId, string userId, Func<IConsentLedger, Task<T>> action)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        return await action(scope.ServiceProvider.GetRequiredService<IConsentLedger>());
    }

    /// <summary>A check as an export would run it: no tenant, no vendor context, an operator or job as the acting user.</summary>
    private static async Task<ConsentCheckResult> Check(
        ModuleHost host, Guid companyId, Guid recipientId, ConsentScope scope, string? actingUserId = null)
    {
        await using var request = host.ScopeFor(tenant: (TenantContext?)null, actingUserId: actingUserId);
        return await request.ServiceProvider.GetRequiredService<IConsentLedger>().CheckAsync(companyId, recipientId, scope, Ct);
    }

    private async Task<(Guid CompanyId, string UserId, Guid RecipientId)> VendorWithRecipientAsync(string nameEn)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Recipient of {nameEn}", Ct);
        return (companyId, userId, recipientId);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
