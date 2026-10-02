using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// QA pass on the consent ledger (vendor plan task 6, F-64, V-12, V-13, ADR-0010, ADR-0011), beside
/// <see cref="ConsentLedgerTests"/>: the listed status and the no-backdating rule turn at midnight in Riyadh (21:00 UTC),
/// not at midnight UTC; a revocation outranks every period status; only the company's vendor admin changes its ledger
/// (the database allows no other vendor role); a tenant related to the company can check but never revoke, even naming the
/// vendor admin as its acting user; the app role cannot put a recipient on the platform list; and the exact fields of the
/// three platform audit entries.
/// <para>
/// Every application clock is derived from a day 60 days after the database's own date (<see cref="DatabaseClock"/>), later
/// than its real date, so each grant the service accepts also passes the database's no-backdating trigger (migration 0015)
/// whatever day the suite runs.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ConsentLedgerQaTests(DatabaseFixture db) : IAsyncLifetime
{
    // The day the tests treat as today in Riyadh (UTC+3 all year), set from the database in InitializeAsync.
    private DateOnly D;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => D = await DatabaseClock.FutureRiyadhDayAsync(db.OwnerConnectionString, Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static TheoryData<int, string, string> MidnightCases => new()
    {
        // Days are relative to D. The day before the first day, one second before and at midnight in Riyadh; UTC is on
        // the day before in both.
        { -1, "20:59:59", "NotYetValid" },
        { -1, "21:00:00", "Active" },
        // The last day (D + 10), one second before and at midnight in Riyadh; UTC is still on the last day in both.
        { 10, "20:59:59", "Active" },
        { 10, "21:00:00", "Expired" },
    };

    [Theory]
    [MemberData(nameof(MidnightCases))]
    public async Task The_listed_status_turns_at_midnight_in_Riyadh_not_at_midnight_UTC(int dayOffset, string utcTime, string expected)
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync($"Midnight {dayOffset} {utcTime}");
        await using (var granting = Host(At(D.AddDays(-1), 9)))
        {
            (await LedgerFor(granting, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, D, D.AddDays(10), userId, Ct)))
                .IsSuccess.ShouldBeTrue();
        }

        await using var host = Host(DatabaseClock.Utc(D.AddDays(dayOffset), TimeOnly.Parse(utcTime, System.Globalization.CultureInfo.InvariantCulture)));

        var listed = (await LedgerFor(host, companyId, userId, l => l.ListAsync(Ct))).ShouldHaveSingleItem();

        listed.Status.ShouldBe(Enum.Parse<ConsentStatus>(expected));
    }

    [Fact]
    public async Task A_grant_from_today_in_Riyadh_is_accepted_half_an_hour_after_midnight_there_while_UTC_is_still_yesterday()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Just After Midnight Company");
        // 00:30 on D in Riyadh is 21:30 on D - 1 in UTC.
        await using var host = Host(DatabaseClock.Utc(D.AddDays(-1), new TimeOnly(21, 30)));

        var fromRiyadhToday = await LedgerFor(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, D, D.AddDays(30), userId, Ct));

        fromRiyadhToday.IsSuccess.ShouldBeTrue(fromRiyadhToday.Error?.Message);
    }

    [Fact]
    public async Task A_grant_from_the_UTC_date_is_backdated_once_it_is_already_tomorrow_in_Riyadh()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Backdated By UTC Company");
        await using var host = Host(DatabaseClock.Utc(D.AddDays(-1), new TimeOnly(21, 30)));

        var fromUtcToday = await LedgerFor(host, companyId, userId, l =>
            l.GrantAsync(recipientId, ConsentScope.AwardRecords, D.AddDays(-1), D.AddDays(30), userId, Ct));

        fromUtcToday.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.InvalidPeriod);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(-1)] // before its first day: would be NotYetValid
    [InlineData(5)] // inside the period: would be Active
    [InlineData(40)] // after its last day: would be Expired
    public async Task A_revoked_grant_is_listed_as_revoked_before_during_and_after_its_period(int dayOffset)
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync($"Revoked Listing {dayOffset}");
        await using (var granting = Host(At(D.AddDays(-2), 9)))
        {
            var grantId = (await LedgerFor(granting, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.PoRecords, D, D.AddDays(30), userId, Ct))).Value;
            (await LedgerFor(granting, companyId, userId, l => l.RevokeAsync(grantId, userId, Ct))).IsSuccess.ShouldBeTrue();
        }

        await using var host = Host(At(D.AddDays(dayOffset), 9));

        (await LedgerFor(host, companyId, userId, l => l.ListAsync(Ct))).ShouldHaveSingleItem().Status.ShouldBe(ConsentStatus.Revoked);
    }

    [Fact]
    public async Task A_vendor_admin_of_another_company_acting_in_this_companys_context_is_refused_as_not_its_admin()
    {
        var (companyId, adminId, recipientId) = await VendorWithRecipientAsync("Guarded Ledger Company");
        var (_, strangerId, _) = await VendorWithRecipientAsync("Stranger Company");
        await using var host = Host(At(D, 9));
        var grantId = (await LedgerFor(host, companyId, adminId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, D, D.AddDays(30), adminId, Ct))).Value;

        var grant = await LedgerFor(host, companyId, strangerId, l => l.GrantAsync(recipientId, ConsentScope.PoRecords, D, D.AddDays(30), strangerId, Ct));
        var revoke = await LedgerFor(host, companyId, strangerId, l => l.RevokeAsync(grantId, strangerId, Ct));

        grant.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.NotVendorAdmin);
        revoke.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.NotVendorAdmin);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().Id.ShouldBe(grantId);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, strangerId, "vendor.consent_granted", Ct)).ShouldBeEmpty();
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, strangerId, "vendor.consent_revoked", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_signed_in_user_with_no_vendor_row_is_refused_as_not_the_vendor_admin()
    {
        var (companyId, _, recipientId) = await VendorWithRecipientAsync("No Row Company");
        var nobody = $"no-vendor-row-{Guid.NewGuid():N}";
        await using var host = Host(At(D, 9));

        var grant = await LedgerFor(host, companyId, nobody, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, D, D.AddDays(30), nobody, Ct));

        grant.Error.ShouldNotBeNull().Code.ShouldBe(ConsentErrors.NotVendorAdmin);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_database_allows_no_vendor_user_role_but_vendor_admin()
    {
        // So a vendor user who is not the admin cannot exist in the MVP: the page's "not the admin" answer is a defence only.
        var (companyId, _, _) = await VendorWithRecipientAsync("Single Role Company");
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            insert into vendor.vendor_users (id, company_id, user_id, role, privacy_notice_version, privacy_notice_culture, privacy_accepted_at)
            values (gen_random_uuid(), @company, @user, 'vendor-user', 'V1', 'en-US', now())
            """, owner);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("user", $"member-{Guid.NewGuid():N}");

        var refused = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        refused.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refused.ConstraintName.ShouldBe("ck_vendor_users_role");
    }

    [Fact]
    public async Task A_related_tenant_that_may_check_a_grant_still_cannot_revoke_it()
    {
        var (companyId, adminId, recipientId) = await VendorWithRecipientAsync("Checked Not Revoked Company");
        var officer = await StaffAsync(TenantRoles.ContractsOfficer);
        await using var host = Host(At(D, 9));
        var grantId = (await LedgerFor(host, companyId, adminId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, D, D.AddDays(30), adminId, Ct))).Value;

        await using (var acme = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                acme.ServiceProvider.GetRequiredService<IConsentLedger>().RevokeAsync(grantId, officer, Ct));
        }

        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().Kind.ShouldBe("grant");
    }

    [Fact]
    public async Task A_tenant_scope_naming_the_vendor_admin_as_its_acting_user_still_cannot_grant_or_revoke()
    {
        // ADR-0011: nothing but the company's own vendor context acts on its behalf; the admin's id alone is not enough.
        var (companyId, adminId, recipientId) = await VendorWithRecipientAsync("Impersonated Admin Company");
        await using var host = Host(At(D, 9));
        var grantId = (await LedgerFor(host, companyId, adminId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, D, D.AddDays(30), adminId, Ct))).Value;

        await using (var acme = host.ScopeFor(TestTenants.Acme, actingUserId: adminId))
        {
            var ledger = acme.ServiceProvider.GetRequiredService<IConsentLedger>();
            await Should.ThrowAsync<InvalidOperationException>(() => ledger.GrantAsync(recipientId, ConsentScope.PoRecords, D, D.AddDays(30), adminId, Ct));
            await Should.ThrowAsync<InvalidOperationException>(() => ledger.RevokeAsync(grantId, adminId, Ct));
        }

        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().Id.ShouldBe(grantId);
    }

    [Theory]
    [InlineData("insert into vendor.recipients (id, name_ar, name_en) values (gen_random_uuid(), 'ممول', 'Self-listed financier')")]
    [InlineData("update vendor.recipients set name_en = 'Renamed' where id = @recipient")]
    [InlineData("delete from vendor.recipients where id = @recipient")]
    public async Task The_app_role_cannot_add_rename_or_remove_a_recipient(string sql)
    {
        // V-13 and ADR-0011: the platform list is maintained by migration, so no request can put a financier on it.
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Listed Recipient {Guid.NewGuid():N}", Ct);
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // Constant SQL from the test's own inline data.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("recipient", recipientId);

        var refused = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task The_grant_audit_names_the_grant_recipient_scope_period_company_and_tenant()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Audited Grant Company");
        await using var host = Host(At(D, 9));

        var grantId = (await LedgerFor(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.ProfileDocuments, D, D.AddDays(90), userId, Ct))).Value;

        var audit = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_granted", Ct)).ShouldHaveSingleItem();
        audit.SubjectType.ShouldBe("vendor_company");
        audit.SubjectId.ShouldBe(companyId.ToString());
        Fields(audit.Data).ShouldBe(Expected(new Dictionary<string, string?>
        {
            ["tenant_id"] = TestTenants.Acme.TenantId.ToString(),
            ["grant_id"] = grantId.ToString(),
            ["recipient_id"] = recipientId.ToString(),
            ["scope"] = "profile_documents",
            ["valid_from"] = D.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["valid_to"] = D.AddDays(90).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        }));
    }

    [Fact]
    public async Task The_revoke_audit_names_the_grant_the_revocation_recipient_scope_and_tenant()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Audited Revoke Company");
        await using var host = Host(At(D, 9));
        var grantId = (await LedgerFor(host, companyId, userId, l => l.GrantAsync(recipientId, ConsentScope.AwardRecords, D, D.AddDays(30), userId, Ct))).Value;

        var revocationId = (await LedgerFor(host, companyId, userId, l => l.RevokeAsync(grantId, userId, Ct))).Value;

        var audit = (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, userId, "vendor.consent_revoked", Ct)).ShouldHaveSingleItem();
        audit.SubjectType.ShouldBe("vendor_company");
        audit.SubjectId.ShouldBe(companyId.ToString());
        Fields(audit.Data).ShouldBe(Expected(new Dictionary<string, string?>
        {
            ["tenant_id"] = TestTenants.Acme.TenantId.ToString(),
            ["grant_id"] = grantId.ToString(),
            ["revocation_id"] = revocationId.ToString(),
            ["recipient_id"] = recipientId.ToString(),
            ["scope"] = "award_records",
        }));
    }

    [Fact]
    public async Task A_refused_check_is_audited_with_no_grant_and_an_allowed_one_with_the_newest_grant_in_force()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Two Grants Company");
        await ConsentRows.InsertGrantAsOwnerAsync(db.OwnerConnectionString, companyId, recipientId, "award_records", -10, 30, userId, Ct);
        var newer = await ConsentRows.InsertGrantAsOwnerAsync(db.OwnerConnectionString, companyId, recipientId, "award_records", 0, 30, userId, Ct);
        await using var host = Host(At(D, 9));
        var refusedBy = $"export-{Guid.NewGuid():N}";
        var allowedBy = $"export-{Guid.NewGuid():N}";

        await CheckAsync(host, companyId, recipientId, ConsentScope.PoRecords, refusedBy);
        var allowed = await CheckAsync(host, companyId, recipientId, ConsentScope.AwardRecords, allowedBy);

        allowed.GrantId.ShouldBe(newer);
        Fields((await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, refusedBy, "vendor.consent_check", Ct)).ShouldHaveSingleItem().Data)
            .ShouldBe(Expected(new Dictionary<string, string?>
            {
                ["tenant_id"] = null,
                ["result"] = "refused",
                ["grant_id"] = null,
                ["recipient_id"] = recipientId.ToString(),
                ["scope"] = "po_records",
            }));
        Fields((await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, allowedBy, "vendor.consent_check", Ct)).ShouldHaveSingleItem().Data)
            .ShouldBe(Expected(new Dictionary<string, string?>
            {
                ["tenant_id"] = null,
                ["result"] = "allowed",
                ["grant_id"] = newer.ToString(),
                ["recipient_id"] = recipientId.ToString(),
                ["scope"] = "award_records",
            }));
    }

    [Fact]
    public async Task A_check_for_another_recipient_is_refused_even_with_a_grant_in_force_for_the_scope()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Named Recipient Company");
        var otherRecipient = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Unnamed Recipient {Guid.NewGuid():N}", Ct);
        await ConsentRows.InsertGrantAsOwnerAsync(db.OwnerConnectionString, companyId, recipientId, "award_records", 0, 30, userId, Ct);
        await using var host = Host(At(D, 9));

        var check = await CheckAsync(host, companyId, otherRecipient, ConsentScope.AwardRecords, $"export-{Guid.NewGuid():N}");

        check.ShouldBe(new ConsentCheckResult(Allowed: false, GrantId: null));
    }

    /// <summary>The audit entry's data as a key-ordered map, so the comparison does not depend on jsonb's key order.</summary>
    private static SortedDictionary<string, string?> Fields(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(
            document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString()),
            StringComparer.Ordinal);
    }

    private static SortedDictionary<string, string?> Expected(Dictionary<string, string?> fields) => new(fields, StringComparer.Ordinal);

    private static DateTimeOffset At(DateOnly riyadhDay, int riyadhHour) =>
        new(riyadhDay.ToDateTime(new TimeOnly(riyadhHour, 0)), TimeSpan.FromHours(3));

    private ModuleHost Host(DateTimeOffset now) => new(db.AppConnectionString, clock: new FixedClock(now));

    private static async Task<T> LedgerFor<T>(ModuleHost host, Guid companyId, string userId, Func<IConsentLedger, Task<T>> action)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        return await action(scope.ServiceProvider.GetRequiredService<IConsentLedger>());
    }

    /// <summary>A check as an export job runs it: no tenant, no vendor context.</summary>
    private static async Task<ConsentCheckResult> CheckAsync(ModuleHost host, Guid companyId, Guid recipientId, ConsentScope scope, string actingUserId)
    {
        await using var request = host.ScopeFor(tenant: null, actingUserId: actingUserId);
        return await request.ServiceProvider.GetRequiredService<IConsentLedger>().CheckAsync(companyId, recipientId, scope, Ct);
    }

    private async Task<(Guid CompanyId, string UserId, Guid RecipientId)> VendorWithRecipientAsync(string nameEn)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Recipient of {nameEn}", Ct);
        return (companyId, userId, recipientId);
    }

    private async Task<string> StaffAsync(string role)
    {
        var subject = $"{role}-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, subject, $"{subject}@acme.test", [role], "active", Ct);
        return subject;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
