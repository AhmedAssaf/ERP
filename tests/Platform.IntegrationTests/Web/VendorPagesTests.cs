using System.Net;
using System.Xml.Linq;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// The vendor home <c>/vendor</c> (plan task 4, F-11, F-12, W-07): the company summary with the relationship to the host
/// tenant, a documents table with one status per required type (current, expired, missing) and the state of a newer
/// file (waiting for the virus scan, or infected), the missing and expired documents named, and an upload per type.
/// In Arabic the page is right to left and shows no resource key and no English sentence.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorPagesTests(DatabaseFixture db)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).DateTime);

    private static readonly Lazy<(Dictionary<string, string> Arabic, Dictionary<string, string> English)> Resources = new(() =>
    {
        var folder = Path.Combine(RepoPaths.Root, "src", "UI", "Platform.UI", "Resources");
        return (Read(Path.Combine(folder, "SharedResource.ar-SA.resx")), Read(Path.Combine(folder, "SharedResource.en-US.resx")));
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Arabic_vendor_pages_render_right_to_left_without_raw_keys()
    {
        var (vendor, companyId) = await VendorAsync("ar", "Arabic Page Trading");
        // Every status the table can show, so each of their texts is checked too.
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, Today.AddDays(-3), "clean", isCurrent: true, Ct);
        await VendorDocumentRows.InsertAsync(
            db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, Today.AddYears(1), "pending_scan", isCurrent: false, Ct, DateTimeOffset.UtcNow.AddMinutes(1));
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, Today.AddYears(1), "infected", isCurrent: false, Ct);
        var applicant = new TestUser(Guid.NewGuid().ToString(), [], "ar", Email: "applicant@vendor.test", EmailVerified: true);

        foreach (var (path, user) in new[] { ("/vendor", vendor), ("/vendor/register/company", applicant) })
        {
            var html = await PageAsync(path, user);

            html.ShouldContain("<html lang=\"ar\" dir=\"rtl\">", Case.Sensitive, path);
            var (arabic, english) = Resources.Value;
            english.Keys.Where(key => html.Contains(key, StringComparison.Ordinal)).ShouldBeEmpty(path);
            // Sentences only; the gallery's samples imitate real data ("Acme Contracting" is the seeded tenant's portal name).
            english
                .Where(p => p.Value.Contains(' ', StringComparison.Ordinal) && p.Value.Length >= 12 && arabic.GetValueOrDefault(p.Key) != p.Value)
                .Where(p => !p.Value.Contains('{', StringComparison.Ordinal))
                .Where(p => !p.Key.StartsWith("Gallery.Sample.", StringComparison.Ordinal))
                .Where(p => html.Contains(p.Value, StringComparison.Ordinal))
                .Select(p => p.Key)
                .ShouldBeEmpty(path);
        }

        var home = await PageAsync("/vendor", vendor);
        home.ShouldContain("شهادة السجل التجاري");
        home.ShouldContain("data-document-status=\"cr_certificate:expired\"");
    }

    [Fact]
    public async Task The_vendor_page_lists_documents_with_the_correct_status_badges()
    {
        var (vendor, companyId) = await VendorAsync("en", "Home Page Trading");
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, Today.AddYears(1), "clean", isCurrent: true, Ct);
        await VendorDocumentRows.InsertAsync(
            db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, Today.AddDays(-1), "clean", isCurrent: true, Ct, DateTimeOffset.UtcNow.AddMinutes(-5));
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, Today.AddYears(2), "pending_scan", isCurrent: false, Ct);
        // An infected file older than the current one is history and shows nothing.
        await VendorDocumentRows.InsertAsync(
            db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, Today.AddYears(1), "infected", isCurrent: false, Ct, DateTimeOffset.UtcNow.AddDays(-1));

        var html = await PageAsync("/vendor", vendor);

        html.ShouldContain("data-relationship=\"pending\"");
        html.ShouldContain("Waiting for approval");
        html.ShouldContain("Home Page Trading");
        html.ShouldContain("شركة الاختبار");
        html.ShouldContain("data-document-status=\"cr_certificate:current\"");
        html.ShouldContain("data-document-status=\"vat_certificate:expired\"");
        html.ShouldContain("data-document-attempt=\"vat_certificate:pending_scan\"");
        html.ShouldNotContain("data-document-attempt=\"cr_certificate:");
        html.ShouldContain("Waiting for the virus check");
        // The compliance notice names the expired document.
        html.ShouldContain("data-blocking=\"vat_certificate:expired\"");
        html.ShouldContain("VAT registration certificate");
        // One upload per required type.
        html.ShouldContain("data-upload-type=\"cr_certificate\"");
        html.ShouldContain("data-upload-type=\"vat_certificate\"");

        var (other, otherId) = await VendorAsync("en", "Second Page Trading");
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, otherId, VendorDocumentTypes.VatCertificate, Today.AddYears(1), "infected", isCurrent: false, Ct);
        await ApproveAsync(otherId);

        var second = await PageAsync("/vendor", other);

        second.ShouldContain("data-relationship=\"approved\"");
        second.ShouldContain("data-document-status=\"cr_certificate:missing\"");
        second.ShouldContain("data-document-status=\"vat_certificate:missing\"");
        second.ShouldContain("data-document-attempt=\"vat_certificate:infected\"");
        second.ShouldContain("data-blocking=\"cr_certificate:missing\"");
        second.ShouldContain("Commercial registration certificate");
        second.ShouldContain("Second Page Trading");
        second.ShouldNotContain("Home Page Trading", Case.Sensitive, "another company's page shows nothing of the first");
    }

    private async Task<string> PageAsync(string path, TestUser user)
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).As(user), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<(TestUser User, Guid CompanyId)> VendorAsync(string locale, string nameEn)
    {
        var subject = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, subject, VendorRows.NewCrNumber(), nameEn, Ct);
        return (new TestUser(subject, ["acme"], locale, RealmRoles: [IdentityClaims.VendorRealmRole], Email: $"{subject}@vendor.test", EmailVerified: true), companyId);
    }

    private async Task ApproveAsync(Guid companyId)
    {
        await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "update vendor.relationships set status = 'approved', approved_by = 'officer' where tenant_id = @tenant and company_id = @company", owner);
        command.Parameters.AddWithValue("tenant", TestTenants.Acme.TenantId);
        command.Parameters.AddWithValue("company", companyId);
        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(file).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);
}
