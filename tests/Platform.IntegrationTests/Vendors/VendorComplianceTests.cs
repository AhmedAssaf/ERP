using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// <see cref="IVendorCompliance.GetBlockingDocumentsAsync"/> (vendor spec section 3; the submission wizard, F-22, asks
/// it): every required document type without a current clean file, or whose current file expired before the date, named
/// in Arabic and English. A file that is still waiting for its scan or was found infected counts as missing.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorComplianceTests(DatabaseFixture db)
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Expired_and_missing_documents_are_reported_by_name()
    {
        var companyId = await CompanyAsync();
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, Today.AddDays(-1), "clean", isCurrent: true, Ct);

        var blocking = await BlockingAsync(companyId, vendorContext: true);

        blocking.Count.ShouldBe(2);
        var cr = blocking.Single(b => b.Type == VendorDocumentTypes.CrCertificate);
        cr.Reason.ShouldBe(BlockingReason.Expired);
        cr.ExpiredOn.ShouldBe(Today.AddDays(-1));
        cr.NameEn.ShouldBe("Commercial registration certificate");
        cr.NameAr.ShouldBe("شهادة السجل التجاري");
        var vat = blocking.Single(b => b.Type == VendorDocumentTypes.VatCertificate);
        vat.Reason.ShouldBe(BlockingReason.Missing);
        vat.ExpiredOn.ShouldBeNull();
        vat.NameEn.ShouldBe("VAT registration certificate");
        vat.NameAr.ShouldBe("شهادة التسجيل في ضريبة القيمة المضافة");
    }

    [Fact]
    public async Task Current_clean_documents_valid_on_the_date_block_nothing()
    {
        var companyId = await CompanyAsync();
        // Valid through the date itself.
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, Today, "clean", isCurrent: true, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, Today.AddYears(1), "clean", isCurrent: true, Ct);

        (await BlockingAsync(companyId, vendorContext: true)).ShouldBeEmpty();
        (await BlockingAsync(companyId, vendorContext: true, onDate: Today.AddDays(1)))
            .ShouldHaveSingleItem().Type.ShouldBe(VendorDocumentTypes.CrCertificate);
    }

    [Fact]
    public async Task Pending_infected_and_superseded_files_do_not_count()
    {
        var companyId = await CompanyAsync();
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, Today.AddYears(1), "pending_scan", isCurrent: false, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, Today.AddYears(1), "infected", isCurrent: false, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, Today.AddYears(1), "clean", isCurrent: false, Ct);

        var blocking = await BlockingAsync(companyId, vendorContext: true);

        blocking.Select(b => (b.Type, b.Reason)).ShouldBe(
            [(VendorDocumentTypes.CrCertificate, BlockingReason.Missing), (VendorDocumentTypes.VatCertificate, BlockingReason.Missing)],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Tenant_staff_get_the_same_answer_only_through_a_relationship()
    {
        var companyId = await CompanyAsync();
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, Today.AddYears(1), "clean", isCurrent: true, Ct);

        // Acme is related (the company registered there): the VAT certificate is missing.
        (await BlockingAsync(companyId, vendorContext: false)).ShouldHaveSingleItem().Type.ShouldBe(VendorDocumentTypes.VatCertificate);

        // Beta is not related and sees no documents at all.
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Beta);
        (await scope.ServiceProvider.GetRequiredService<IVendorCompliance>().GetBlockingDocumentsAsync(companyId, Today, Ct))
            .Count.ShouldBe(2);
    }

    [Fact]
    public async Task Without_a_tenant_or_the_companys_own_vendor_context_the_question_is_refused()
    {
        var companyId = await CompanyAsync();
        var otherCompany = await CompanyAsync();
        await using var host = new ModuleHost(db.AppConnectionString);

        await using (var none = host.ScopeFor(tenant: null))
        {
            await Should.ThrowAsync<InvalidOperationException>(
                () => none.ServiceProvider.GetRequiredService<IVendorCompliance>().GetBlockingDocumentsAsync(companyId, Today, Ct));
        }

        await using var other = host.ScopeFor(tenant: null, vendorCompanyId: otherCompany);
        await Should.ThrowAsync<InvalidOperationException>(
            () => other.ServiceProvider.GetRequiredService<IVendorCompliance>().GetBlockingDocumentsAsync(companyId, Today, Ct));
    }

    private async Task<IReadOnlyList<BlockingDocument>> BlockingAsync(Guid companyId, bool vendorContext, DateOnly? onDate = null)
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, vendorContext ? companyId : null);
        return await scope.ServiceProvider.GetRequiredService<IVendorCompliance>().GetBlockingDocumentsAsync(companyId, onDate ?? Today, Ct);
    }

    private Task<Guid> CompanyAsync() =>
        VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), VendorRows.NewCrNumber(), "Compliance Company", Ct);
}
