using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// QA pass on vendor plan task 5 (F-10 as narrowed, V-7, V-11, ADR-0008): the tenant view's security-definer functions
/// (<c>vendor.related_companies()</c>, <c>vendor.related_current_documents()</c>, with the 0001 pair
/// <c>related_company</c> and <c>related_documents</c>) probed directly as the app role, so tenant isolation holds without
/// the web host's policies; document expiry against the Riyadh calendar day at its UTC boundary (21:00 UTC); and the
/// company's audit trail across two tenants.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorDirectoryQaTests(DatabaseFixture db)
{
    private static readonly DateOnly ExpiresOn = new(2026, 9, 27);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Related_companies_and_current_documents_return_no_rows_without_a_tenant()
    {
        var (companyId, _) = await VendorAsync("No Tenant Probe", TestTenants.Acme);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, await DatabaseClock.ValidUntilAsync(db.OwnerConnectionString, Ct), "clean", isCurrent: true, Ct);

        var probe = await ProbeAsync(tenantId: null, vendorCompanyId: null, userId: null, companyId);

        probe.Companies.ShouldBeEmpty();
        probe.DocumentCompanies.ShouldBeEmpty();
        probe.CardRows.ShouldBe(0);
        probe.DocumentRows.ShouldBe(0);
    }

    [Fact]
    public async Task Acme_never_reads_a_beta_only_company_or_its_documents_through_the_directory_functions()
    {
        var (betaOnlyId, _) = await VendorAsync("Beta Supplies Co", TestTenants.Beta);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, betaOnlyId, VendorDocumentTypes.CrCertificate, await DatabaseClock.ValidUntilAsync(db.OwnerConnectionString, Ct), "clean", isCurrent: true, Ct);

        var acme = await ProbeAsync(TestTenants.Acme.TenantId, vendorCompanyId: null, userId: null, betaOnlyId);
        var beta = await ProbeAsync(TestTenants.Beta.TenantId, vendorCompanyId: null, userId: null, betaOnlyId);

        acme.Companies.ShouldNotContain(betaOnlyId);
        acme.DocumentCompanies.ShouldNotContain(betaOnlyId);
        acme.CardRows.ShouldBe(0);
        acme.DocumentRows.ShouldBe(0);
        // Positive control: the tenant it registered with reads both.
        beta.Companies.ShouldContain(betaOnlyId);
        beta.DocumentCompanies.ShouldContain(betaOnlyId);
        beta.CardRows.ShouldBe(1);
        beta.DocumentRows.ShouldBe(1);
    }

    [Fact]
    public async Task Related_current_documents_hands_out_only_the_current_clean_file_of_each_type()
    {
        var (companyId, _) = await VendorAsync("Current Files Only", TestTenants.Acme);
        var validUntil = await DatabaseClock.ValidUntilAsync(db.OwnerConnectionString, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, validUntil, "clean", isCurrent: true, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, new DateOnly(2025, 1, 31), "clean", isCurrent: false, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, validUntil, "pending_scan", isCurrent: false, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, validUntil, "infected", isCurrent: false, Ct);

        var rows = await CurrentDocumentsAsync(TestTenants.Acme.TenantId, companyId);

        rows.ShouldBe([(VendorDocumentTypes.CrCertificate, validUntil)]);
    }

    [Fact]
    public async Task A_vendor_context_on_the_tenant_host_reads_no_other_company_through_the_directory_functions()
    {
        // Two competitors of the same tenant: the vendor row-level security keeps each to its own company (task 1); the
        // tenant view's security-definer functions must not open a way round it for a vendor session on that host.
        var (ownId, ownUser) = await VendorAsync("First Supplier Est", TestTenants.Acme);
        var (competitorId, _) = await VendorAsync("Second Competitor Co", TestTenants.Acme);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, competitorId, VendorDocumentTypes.CrCertificate, await DatabaseClock.ValidUntilAsync(db.OwnerConnectionString, Ct), "clean", isCurrent: true, Ct);

        var probe = await ProbeAsync(TestTenants.Acme.TenantId, ownId, ownUser, competitorId);

        probe.Companies.ShouldNotContain(competitorId);
        probe.DocumentCompanies.ShouldNotContain(competitorId);
        probe.CardRows.ShouldBe(0);
        probe.DocumentRows.ShouldBe(0);
    }

    [Theory]
    [InlineData("2026-09-26T21:00:00Z", false)] // 00:00 on the 27th in Riyadh: the expiry day itself, still valid
    [InlineData("2026-09-27T20:59:59Z", false)] // 23:59:59 on the 27th in Riyadh
    [InlineData("2026-09-27T21:00:00Z", true)] // 00:00 on the 28th in Riyadh, still the 27th in UTC
    [InlineData("2026-09-27T23:59:59Z", true)] // 02:59:59 on the 28th in Riyadh, still the 27th in UTC
    public async Task The_tenant_list_blocks_a_document_from_riyadh_midnight_after_its_expiry_date(string utcNow, bool blocked)
    {
        var companyId = await CompanyWithCrExpiringAsync("Riyadh Midnight List");
        await using var host = new ModuleHost(db.AppConnectionString, clock: new FixedClock(utcNow));
        await using var scope = host.ScopeFor(TestTenants.Acme);

        var vendor = (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ListRelatedAsync(Ct)).Single(v => v.Id == companyId);

        vendor.BlockingDocuments.Select(b => (b.Type, b.Reason, b.ExpiredOn)).ShouldBe(
            blocked ? [(VendorDocumentTypes.CrCertificate, BlockingReason.Expired, (DateOnly?)ExpiresOn)] : []);
    }

    [Theory]
    [InlineData("2026-09-27T20:59:59Z", false)]
    [InlineData("2026-09-27T21:00:00Z", true)]
    public async Task The_vendor_card_blocks_a_document_from_riyadh_midnight_after_its_expiry_date(string utcNow, bool blocked)
    {
        var companyId = await CompanyWithCrExpiringAsync("Riyadh Midnight Card");
        await using var host = new ModuleHost(db.AppConnectionString, clock: new FixedClock(utcNow));
        await using var scope = host.ScopeFor(TestTenants.Acme);

        var card = (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().GetRelatedAsync(companyId, Ct)).ShouldNotBeNull();

        card.BlockingDocuments.Select(b => (b.Type, b.Reason, b.ExpiredOn)).ShouldBe(
            blocked ? [(VendorDocumentTypes.CrCertificate, BlockingReason.Expired, (DateOnly?)ExpiresOn)] : []);
    }

    [Fact]
    public async Task The_company_audit_trail_has_each_action_once_in_the_tenant_where_it_happened_with_its_actor()
    {
        var (companyId, userId) = await VendorAsync("Audited Across Tenants", TestTenants.Acme);
        var officer = $"officer-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, officer, $"{officer}@acme.example.sa", [TenantRoles.ContractsOfficer], "active", Ct);
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]) };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, officer, Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (var scope = host.ScopeFor(TestTenants.Beta, companyId, userId))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct)).IsSuccess.ShouldBeTrue();
        }

        // Keycloak no longer lists the user in acme's organization: joining acme again is refused (W-21 pentest P-1,
        // restoring access is acme's decision) and the refusal is audited in acme's log.
        accounts.State = new(HoldsVendorRole: true, OrganizationAliases: ["beta"]);
        await using (var scope = host.ScopeFor(TestTenants.Acme, companyId, userId))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct)).Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.MembershipRemoved);
        }

        (await CompanyAuditTrailAsync(companyId)).ShouldBe(
            [
                (TestTenants.Acme.TenantId, officer, "vendor.approved"),
                (TestTenants.Acme.TenantId, userId, "vendor.membership_restore_refused"),
                (TestTenants.Beta.TenantId, userId, "vendor.joined"),
            ],
            ignoreOrder: true);
    }

    private async Task<Guid> CompanyWithCrExpiringAsync(string nameEn)
    {
        var (companyId, _) = await VendorAsync(nameEn, TestTenants.Acme);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, ExpiresOn, "clean", isCurrent: true, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, new DateOnly(2099, 12, 31), "clean", isCurrent: true, Ct);
        return companyId;
    }

    private async Task<(Guid CompanyId, string UserId)> VendorAsync(string nameEn, TenantContext tenant)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, tenant, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        // W-33: approval needs a verified owner; these tests are about approval itself (CrOwnershipTests covers the check).
        await OwnershipRows.VerifyAsOwnerAsync(db.OwnerConnectionString, companyId, tenant.TenantId, Ct);
        return (companyId, userId);
    }

    /// <summary>What the app role reads through the four tenant-view functions with the given session settings.</summary>
    private async Task<(List<Guid> Companies, List<Guid> DocumentCompanies, int CardRows, int DocumentRows)> ProbeAsync(
        Guid? tenantId, Guid? vendorCompanyId, string? userId, Guid companyId)
    {
        await using var connection = await AppConnectionAsync(tenantId, vendorCompanyId, userId);
        var companies = await GuidsAsync(connection, "select id from vendor.related_companies()");
        var documentCompanies = await GuidsAsync(connection, "select company_id from vendor.related_current_documents()");
        var cardRows = await CountAsync(connection, "select count(*)::int from vendor.related_company(@company)", companyId);
        var documentRows = await CountAsync(connection, "select count(*)::int from vendor.related_documents(@company)", companyId);
        return (companies, documentCompanies, cardRows, documentRows);
    }

    private async Task<List<(string Type, DateOnly ExpiresOn)>> CurrentDocumentsAsync(Guid tenantId, Guid companyId)
    {
        await using var connection = await AppConnectionAsync(tenantId, null, null);
        await using var command = new NpgsqlCommand(
            "select type, expires_on from vendor.related_current_documents() where company_id = @company order by type", connection);
        command.Parameters.AddWithValue("company", companyId);
        var rows = new List<(string, DateOnly)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetFieldValue<DateOnly>(1)));
        }

        return rows;
    }

    /// <summary>The app role with the session settings the connection interceptor sets (empty when absent).</summary>
    private async Task<NpgsqlConnection> AppConnectionAsync(Guid? tenantId, Guid? vendorCompanyId, string? userId)
    {
        var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select set_config('app.tenant_id', @tenant, false),
                   set_config('app.vendor_company_id', @vendor, false),
                   set_config('app.user_id', @user, false)
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("vendor", vendorCompanyId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("user", userId ?? string.Empty);
        await command.ExecuteNonQueryAsync(Ct);
        return connection;
    }

    private static async Task<List<Guid>> GuidsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<int> CountAsync(NpgsqlConnection connection, string sql, Guid companyId)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("company", companyId);
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Every <c>vendor.*</c> audit event about the company in any tenant's log, as the owner reads it.</summary>
    private async Task<List<(Guid TenantId, string ActorId, string Action)>> CompanyAuditTrailAsync(Guid companyId)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select tenant_id, actor_id, action from audit.events
            where subject_type = 'vendor_company' and subject_id = @company and action like 'vendor.%'
            """, connection);
        command.Parameters.AddWithValue("company", companyId.ToString());
        var rows = new List<(Guid, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private sealed class FixedClock(string utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _now = DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
