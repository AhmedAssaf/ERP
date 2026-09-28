using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// What the vendor home (<c>/vendor</c>, plan task 4) reads besides the clean documents: the company's relationship with
/// the host tenant, and the files that are not listed yet (waiting for the virus scan) or never will be (infected), so
/// the documents table can say so instead of showing a newly uploaded file as missing.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorHomeDataTests(DatabaseFixture db)
{
    private static readonly DateOnly NextYear = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_current_company_carries_its_relationship_with_the_host_tenant()
    {
        var companyId = await CompanyAsync();
        await using var host = new ModuleHost(db.AppConnectionString);

        await using (var acme = host.ScopeFor(TestTenants.Acme, companyId))
        {
            var company = (await acme.ServiceProvider.GetRequiredService<IVendorCompanies>().CurrentAsync(Ct)).ShouldNotBeNull();
            company.Relationship.ShouldBe(VendorRelationshipStatus.Pending);
        }

        await ApproveAsync(TestTenants.Acme.TenantId, companyId);
        await using (var acme = host.ScopeFor(TestTenants.Acme, companyId))
        {
            (await acme.ServiceProvider.GetRequiredService<IVendorCompanies>().CurrentAsync(Ct))!.Relationship.ShouldBe(VendorRelationshipStatus.Approved);
        }

        // Beta has no relationship with the company: nothing to show there.
        await using var beta = host.ScopeFor(TestTenants.Beta, companyId);
        (await beta.ServiceProvider.GetRequiredService<IVendorCompanies>().CurrentAsync(Ct))!.Relationship.ShouldBeNull();
    }

    [Fact]
    public async Task Files_waiting_for_a_scan_or_found_infected_are_listed_as_not_clean_for_their_own_company_only()
    {
        var companyId = await CompanyAsync();
        var otherId = await CompanyAsync();
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, NextYear, "clean", isCurrent: true, Ct);
        await VendorDocumentRows.InsertAsync(
            db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, NextYear.AddDays(1), "pending_scan", isCurrent: false, Ct,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        await VendorDocumentRows.InsertAsync(
            db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, NextYear.AddDays(2), "infected", isCurrent: false, Ct,
            DateTimeOffset.UtcNow.AddMinutes(-2));
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, otherId, VendorDocumentTypes.VatCertificate, NextYear, "pending_scan", isCurrent: false, Ct);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId);

        var notClean = await scope.ServiceProvider.GetRequiredService<IVendorDocuments>().ListNotCleanAsync(Ct);

        notClean.Select(d => (d.Type, d.ExpiresOn, d.ScanState)).ShouldBe(
        [
            (VendorDocumentTypes.CrCertificate, NextYear.AddDays(1), VendorDocumentScanState.PendingScan),
            (VendorDocumentTypes.VatCertificate, NextYear.AddDays(2), VendorDocumentScanState.Infected),
        ]);
    }

    private Task<Guid> CompanyAsync() =>
        VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), VendorRows.NewCrNumber(), "Home Data Trading", Ct);

    private async Task ApproveAsync(Guid tenantId, Guid companyId)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "update vendor.relationships set status = 'approved', approved_by = 'officer' where tenant_id = @tenant and company_id = @company", owner);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("company", companyId);
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }
}
